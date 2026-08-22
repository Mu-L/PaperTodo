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
/// Session-scoped plugin event hub. The host keeps a baseline only while subscriptions exist and
/// never polls the whole workspace on a recurring interval. User mutations are detected through
/// AppController's monotonic state revision; a short one-shot debounce coalesces edit bursts before
/// the expensive snapshot/diff. MCP/plugin mutations still publish synchronously with their exact
/// operation origin.
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

    private static readonly TimeSpan UserChangeDebounce = TimeSpan.FromMilliseconds(180);

    private readonly AppController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _flushTimer;
    private readonly Dictionary<Guid, Subscription> _subscriptions = [];
    private Dictionary<string, PaperStateSnapshot> _baseline =
        new(StringComparer.Ordinal);
    private long _observedStateRevision;
    private long _scheduledStateRevision;
    private int _suppressionDepth;
    private bool _watchingDispatcher;
    private bool _disposed;

    public PaperBodyPluginEventHub(AppController controller, Dispatcher dispatcher)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _flushTimer = new DispatcherTimer(
            DispatcherPriority.ContextIdle,
            dispatcher)
        {
            Interval = UserChangeDebounce
        };
        _flushTimer.Tick += OnFlushTimerTick;
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        var wasEmpty = _subscriptions.Count == 0;
        var subscription = new Subscription(
            Guid.NewGuid(),
            sessionId,
            providerId,
            filter,
            handler);
        _subscriptions.Add(subscription.Id, subscription);
        if (wasEmpty)
        {
            _baseline = CaptureState();
            _observedStateRevision = _controller.PluginEventStateRevision;
            _scheduledStateRevision = _observedStateRevision;
            StartWatchingDispatcher();
        }
        return new SubscriptionHandle(this, subscription.Id);
    }

    public void FlushUserChanges()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _subscriptions.Count == 0 || _suppressionDepth > 0)
        {
            return;
        }

        var revision = _controller.PluginEventStateRevision;
        if (revision == _observedStateRevision)
        {
            return;
        }

        _flushTimer.Stop();
        ScanNowCore(PaperOperationContext.User(), revision);
    }

    public IDisposable SuppressScans()
    {
        _dispatcher.VerifyAccess();
        _suppressionDepth++;
        return new CallbackScope(() =>
        {
            _dispatcher.VerifyAccess();
            _suppressionDepth = Math.Max(0, _suppressionDepth - 1);
            if (_suppressionDepth == 0)
            {
                QueueUserChangesIfNeeded();
            }
        });
    }

    public void ScanNow(PaperOperationContext context)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _subscriptions.Count == 0 || _suppressionDepth > 0)
        {
            return;
        }

        _flushTimer.Stop();
        ScanNowCore(context, _controller.PluginEventStateRevision);
    }

    public void ResetBaseline()
    {
        _dispatcher.VerifyAccess();
        _flushTimer.Stop();
        if (_subscriptions.Count == 0)
        {
            _baseline.Clear();
            return;
        }

        _baseline = CaptureState();
        _observedStateRevision = _controller.PluginEventStateRevision;
        _scheduledStateRevision = _observedStateRevision;
    }

    public void RemoveSession(Guid sessionId)
    {
        _dispatcher.VerifyAccess();
        foreach (var id in _subscriptions
                     .Where(pair => pair.Value.SessionId == sessionId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _subscriptions.Remove(id);
        }
        StopWhenIdle();
    }

    private void StartWatchingDispatcher()
    {
        if (_watchingDispatcher)
        {
            return;
        }
        _dispatcher.Hooks.OperationCompleted += OnDispatcherOperationCompleted;
        _watchingDispatcher = true;
    }

    private void StopWatchingDispatcher()
    {
        if (!_watchingDispatcher)
        {
            return;
        }
        _dispatcher.Hooks.OperationCompleted -= OnDispatcherOperationCompleted;
        _watchingDispatcher = false;
    }

    private void OnDispatcherOperationCompleted(
        object? sender,
        DispatcherHookEventArgs e)
    {
        if (_disposed || _subscriptions.Count == 0 || _suppressionDepth > 0)
        {
            return;
        }
        QueueUserChangesIfNeeded();
    }

    private void QueueUserChangesIfNeeded()
    {
        if (_disposed || _subscriptions.Count == 0 || _suppressionDepth > 0)
        {
            return;
        }

        var revision = _controller.PluginEventStateRevision;
        if (revision == _observedStateRevision && revision == _scheduledStateRevision)
        {
            return;
        }

        _scheduledStateRevision = revision;
        _flushTimer.Stop();
        _flushTimer.Start();
    }

    private void OnFlushTimerTick(object? sender, EventArgs e)
    {
        _flushTimer.Stop();
        if (_disposed || _subscriptions.Count == 0 || _suppressionDepth > 0)
        {
            return;
        }

        var revision = _controller.PluginEventStateRevision;
        if (revision == _observedStateRevision)
        {
            _scheduledStateRevision = revision;
            return;
        }
        ScanNowCore(PaperOperationContext.User(), revision);
    }

    private void ScanNowCore(PaperOperationContext context, long observedRevision)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _suppressionDepth > 0 || _subscriptions.Count == 0)
        {
            return;
        }

        var subscribers = _subscriptions.Values.ToArray();
        var previous = _baseline;
        var current = CaptureState();
        var events = BuildEvents(previous, current, context);
        if (_disposed || _subscriptions.Count == 0)
        {
            return;
        }

        _baseline = current;
        _observedStateRevision = observedRevision;
        _scheduledStateRevision = observedRevision;

        foreach (var value in events)
        {
            foreach (var subscriber in subscribers)
            {
                if (_disposed || !_subscriptions.ContainsKey(subscriber.Id) ||
                    !Matches(subscriber, value))
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

        // A handler may synchronously mutate PaperTodo. Do not lose that newer revision just
        // because this scan installed a fresh baseline before invoking listeners.
        QueueUserChangesIfNeeded();
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
        _subscriptions.Remove(id);
        StopWhenIdle();
    }

    private void StopWhenIdle()
    {
        if (_subscriptions.Count != 0)
        {
            return;
        }

        _flushTimer.Stop();
        StopWatchingDispatcher();
        _baseline.Clear();
        _observedStateRevision = _controller.PluginEventStateRevision;
        _scheduledStateRevision = _observedStateRevision;
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
        _flushTimer.Stop();
        _flushTimer.Tick -= OnFlushTimerTick;
        StopWatchingDispatcher();
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
