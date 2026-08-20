using System.Windows.Threading;
using PaperTodo.Plugin;

namespace PaperTodo;

internal readonly record struct PaperOperationContext(
    PaperTodoEventOrigin Origin,
    string? SourcePluginId,
    Guid OperationId,
    DateTimeOffset OccurredAt)
{
    public static PaperOperationContext User() =>
        new(PaperTodoEventOrigin.User, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

    public static PaperOperationContext Mcp() =>
        new(PaperTodoEventOrigin.Mcp, null, Guid.NewGuid(), DateTimeOffset.UtcNow);

    public static PaperOperationContext Plugin(string providerId) =>
        new(PaperTodoEventOrigin.Plugin, providerId, Guid.NewGuid(), DateTimeOffset.UtcNow);
}

/// <summary>
/// Session-scoped plugin event hub. No polling or snapshots exist until the first active
/// subscription. The timer runs only while at least one visible plugin session is subscribed.
/// All access occurs on PaperTodo's UI dispatcher.
/// </summary>
internal sealed class PaperBodyPluginEventHub : IDisposable
{
    private sealed record Subscription(
        Guid Id,
        Guid SessionId,
        string ProviderId,
        PaperTodoEventFilter Filter,
        Action<PaperTodoEvent> Handler);

    private sealed record PaperStateSnapshot(
        PaperSnapshot Paper,
        IReadOnlyDictionary<string, TodoSnapshot> Todos,
        string? NoteContent);

    private readonly object _gate = new();
    private readonly AppController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _scanTimer;
    private readonly Dictionary<Guid, Subscription> _subscriptions = [];
    private Dictionary<string, PaperStateSnapshot> _baseline =
        new(StringComparer.Ordinal);
    private int _suppressionDepth;
    private bool _disposed;

    public PaperBodyPluginEventHub(AppController controller, Dispatcher dispatcher)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _scanTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.ContextIdle,
            (_, _) => ScanNowCore(PaperOperationContext.User()),
            dispatcher);
        _scanTimer.Stop();
    }

    public IDisposable Subscribe(
        Guid sessionId,
        string providerId,
        PaperTodoEventFilter filter,
        Action<PaperTodoEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(handler);
        _dispatcher.VerifyAccess();

        var subscription = new Subscription(
            Guid.NewGuid(),
            sessionId,
            providerId,
            filter,
            handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var wasEmpty = _subscriptions.Count == 0;
            _subscriptions.Add(subscription.Id, subscription);
            if (wasEmpty)
            {
                _baseline = CaptureState();
                _scanTimer.Start();
            }
        }
        return new SubscriptionHandle(this, subscription.Id);
    }

    public void FlushUserChanges()
    {
        _dispatcher.VerifyAccess();
        ScanNowCore(PaperOperationContext.User());
    }

    public IDisposable SuppressScans()
    {
        _dispatcher.VerifyAccess();
        _suppressionDepth++;
        return new CallbackScope(() =>
        {
            _dispatcher.VerifyAccess();
            _suppressionDepth = Math.Max(0, _suppressionDepth - 1);
        });
    }

    public void ScanNow(PaperOperationContext context)
    {
        _dispatcher.VerifyAccess();
        ScanNowCore(context);
    }

    public void ResetBaseline()
    {
        _dispatcher.VerifyAccess();
        lock (_gate)
        {
            _baseline = _subscriptions.Count == 0
                ? new Dictionary<string, PaperStateSnapshot>(StringComparer.Ordinal)
                : CaptureState();
        }
    }

    public void RemoveSession(Guid sessionId)
    {
        _dispatcher.VerifyAccess();
        lock (_gate)
        {
            foreach (var id in _subscriptions
                         .Where(pair => pair.Value.SessionId == sessionId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _subscriptions.Remove(id);
            }
            StopWhenIdle();
        }
    }

    private void ScanNowCore(PaperOperationContext context)
    {
        _dispatcher.VerifyAccess();
        Subscription[] subscribers;
        Dictionary<string, PaperStateSnapshot> previous;
        lock (_gate)
        {
            if (_disposed || _suppressionDepth > 0 || _subscriptions.Count == 0)
            {
                return;
            }
            subscribers = _subscriptions.Values.ToArray();
            previous = _baseline;
        }

        var current = CaptureState();
        var events = BuildEvents(previous, current, context);
        lock (_gate)
        {
            if (_disposed || _subscriptions.Count == 0)
            {
                return;
            }
            _baseline = current;
        }

        foreach (var value in events)
        {
            foreach (var subscriber in subscribers)
            {
                lock (_gate)
                {
                    if (_disposed || !_subscriptions.ContainsKey(subscriber.Id))
                    {
                        continue;
                    }
                }
                if (!Matches(subscriber, value))
                {
                    continue;
                }
                try
                {
                    subscriber.Handler(value);
                }
                catch
                {
                    // One plugin listener cannot affect PaperTodo or another listener.
                }
            }
        }
    }

    private Dictionary<string, PaperStateSnapshot> CaptureState()
    {
        var result = new Dictionary<string, PaperStateSnapshot>(StringComparer.Ordinal);
        foreach (var paper in _controller.State.Papers)
        {
            if (string.IsNullOrWhiteSpace(paper.Id))
            {
                continue;
            }

            var todos = new Dictionary<string, TodoSnapshot>(StringComparer.Ordinal);
            if (paper.Type == PaperTypes.Todo)
            {
                foreach (var item in paper.Items)
                {
                    if (!IsObservableTodo(item) || string.IsNullOrWhiteSpace(item.Id))
                    {
                        continue;
                    }
                    // Assignment, rather than ToDictionary, keeps a malformed duplicate id from
                    // taking down the listener runtime.
                    todos[item.Id] = _controller.CaptureTodoSnapshot(paper, item);
                }
            }

            var noteContent = paper.Type == PaperTypes.Note &&
                              string.Equals(
                                  paper.BodyProviderId,
                                  PaperBodyProviderIds.Markdown,
                                  StringComparison.Ordinal)
                ? paper.Content ?? ""
                : null;
            result[paper.Id] = new PaperStateSnapshot(
                _controller.CapturePaperSnapshot(paper),
                todos,
                noteContent);
        }
        return result;
    }

    internal static bool IsObservableTodo(PaperItem item) =>
        TodoRules.HasMeaningfulContent(item);

    private static IReadOnlyList<PaperTodoEvent> BuildEvents(
        IReadOnlyDictionary<string, PaperStateSnapshot> before,
        IReadOnlyDictionary<string, PaperStateSnapshot> after,
        PaperOperationContext context)
    {
        var events = new List<PaperTodoEvent>();

        foreach (var paperId in before.Keys.Except(after.Keys, StringComparer.Ordinal))
        {
            var oldPaper = before[paperId];
            foreach (var todo in oldPaper.Todos.Values.OrderBy(item => item.Order))
            {
                events.Add(new TodoDeletedEvent(todo, Metadata(context)));
            }
            events.Add(new PaperDeletedEvent(oldPaper.Paper, Metadata(context)));
        }

        foreach (var paperId in after.Keys.Except(before.Keys, StringComparer.Ordinal))
        {
            var newPaper = after[paperId];
            events.Add(new PaperCreatedEvent(newPaper.Paper, Metadata(context)));
            foreach (var todo in newPaper.Todos.Values.OrderBy(item => item.Order))
            {
                events.Add(new TodoCreatedEvent(todo, Metadata(context)));
            }
            if (newPaper.NoteContent is { Length: > 0 } content)
            {
                events.Add(new NoteChangedEvent(
                    newPaper.Paper.Id,
                    newPaper.Paper.Title,
                    0,
                    content.Length,
                    Metadata(context)));
            }
        }

        foreach (var paperId in before.Keys.Intersect(after.Keys, StringComparer.Ordinal))
        {
            var oldPaper = before[paperId];
            var newPaper = after[paperId];
            var paperFields = ChangedPaperFields(oldPaper.Paper, newPaper.Paper);
            if (paperFields != PaperChangedFields.None)
            {
                events.Add(new PaperChangedEvent(
                    oldPaper.Paper,
                    newPaper.Paper,
                    paperFields,
                    Metadata(context)));
            }

            foreach (var todoId in oldPaper.Todos.Keys.Except(newPaper.Todos.Keys, StringComparer.Ordinal))
            {
                events.Add(new TodoDeletedEvent(oldPaper.Todos[todoId], Metadata(context)));
            }
            foreach (var todoId in newPaper.Todos.Keys.Except(oldPaper.Todos.Keys, StringComparer.Ordinal))
            {
                events.Add(new TodoCreatedEvent(newPaper.Todos[todoId], Metadata(context)));
            }
            foreach (var todoId in oldPaper.Todos.Keys.Intersect(newPaper.Todos.Keys, StringComparer.Ordinal))
            {
                var oldTodo = oldPaper.Todos[todoId];
                var newTodo = newPaper.Todos[todoId];
                var fields = ChangedTodoFields(oldTodo, newTodo);
                if (fields != TodoChangedFields.None)
                {
                    events.Add(new TodoChangedEvent(
                        oldTodo,
                        newTodo,
                        fields,
                        Metadata(context)));
                }
            }

            if (!string.Equals(oldPaper.NoteContent, newPaper.NoteContent, StringComparison.Ordinal))
            {
                events.Add(new NoteChangedEvent(
                    newPaper.Paper.Id,
                    newPaper.Paper.Title,
                    oldPaper.NoteContent?.Length ?? 0,
                    newPaper.NoteContent?.Length ?? 0,
                    Metadata(context)));
            }
        }
        return events;
    }

    private static PaperChangedFields ChangedPaperFields(PaperSnapshot before, PaperSnapshot after)
    {
        var fields = PaperChangedFields.None;
        if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal)) fields |= PaperChangedFields.Title;
        if (before.IsVisible != after.IsVisible) fields |= PaperChangedFields.Visibility;
        if (before.IsCollapsed != after.IsCollapsed) fields |= PaperChangedFields.Collapsed;
        if (before.AlwaysOnTop != after.AlwaysOnTop) fields |= PaperChangedFields.AlwaysOnTop;
        if (!string.Equals(before.BodyProviderId, after.BodyProviderId, StringComparison.Ordinal)) fields |= PaperChangedFields.BodyProvider;
        return fields;
    }

    private static TodoChangedFields ChangedTodoFields(TodoSnapshot before, TodoSnapshot after)
    {
        var fields = TodoChangedFields.None;
        if (!string.Equals(before.Text, after.Text, StringComparison.Ordinal)) fields |= TodoChangedFields.Text;
        if (before.Done != after.Done) fields |= TodoChangedFields.Completion;
        if (before.Order != after.Order) fields |= TodoChangedFields.Order;
        if (before.ReminderAt != after.ReminderAt) fields |= TodoChangedFields.Reminder;
        if (!string.Equals(before.LinkedPaperId, after.LinkedPaperId, StringComparison.Ordinal)) fields |= TodoChangedFields.LinkedPaper;
        if (!string.Equals(before.LinkedPath, after.LinkedPath, StringComparison.Ordinal)) fields |= TodoChangedFields.LinkedPath;
        return fields;
    }

    private static PaperTodoEventMetadata Metadata(PaperOperationContext context) =>
        new(Guid.NewGuid(), context.OperationId, context.OccurredAt, context.Origin, context.SourcePluginId);

    private static bool Matches(Subscription subscription, PaperTodoEvent value)
    {
        var filter = subscription.Filter;
        if (filter.ExcludeOwnOperations &&
            value.Metadata.Origin == PaperTodoEventOrigin.Plugin &&
            string.Equals(value.Metadata.SourcePluginId, subscription.ProviderId, StringComparison.Ordinal))
        {
            return false;
        }
        if (filter.Kinds is { Count: > 0 } kinds && !kinds.Contains(value.Kind))
        {
            return false;
        }
        return filter.PaperIds is not { Count: > 0 } paperIds ||
               paperIds.Contains(EventPaperId(value));
    }

    private static string EventPaperId(PaperTodoEvent value) => value switch
    {
        PaperCreatedEvent item => item.Paper.Id,
        PaperChangedEvent item => item.After.Id,
        PaperDeletedEvent item => item.Paper.Id,
        TodoCreatedEvent item => item.Todo.PaperId,
        TodoChangedEvent item => item.After.PaperId,
        TodoDeletedEvent item => item.Todo.PaperId,
        NoteChangedEvent item => item.PaperId,
        _ => ""
    };

    private void Unsubscribe(Guid id)
    {
        _dispatcher.VerifyAccess();
        lock (_gate)
        {
            _subscriptions.Remove(id);
            StopWhenIdle();
        }
    }

    private void StopWhenIdle()
    {
        if (_subscriptions.Count != 0)
        {
            return;
        }
        _scanTimer.Stop();
        _baseline.Clear();
    }

    public void Dispose()
    {
        if (_dispatcher.CheckAccess())
        {
            DisposeCore();
            return;
        }
        _dispatcher.Invoke(DisposeCore);
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        _scanTimer.Stop();
        _subscriptions.Clear();
        _baseline.Clear();
    }

    private sealed class SubscriptionHandle : IDisposable
    {
        private PaperBodyPluginEventHub? _owner;
        private readonly Guid _id;

        public SubscriptionHandle(PaperBodyPluginEventHub owner, Guid id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null) return;
            if (owner._dispatcher.CheckAccess()) owner.Unsubscribe(_id);
            else owner._dispatcher.Invoke(() => owner.Unsubscribe(_id));
        }
    }

    private sealed class CallbackScope : IDisposable
    {
        private Action? _callback;
        public CallbackScope(Action callback) => _callback = callback;
        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }
}
