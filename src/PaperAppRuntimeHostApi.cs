using PaperTodo.Plugin;

namespace PaperTodo;

/// <summary>
/// Hides paper-session presentation capabilities from app runtimes while reusing the same reviewed
/// Workspace implementation and permission/event semantics.
/// </summary>
internal sealed class PaperAppRuntimeWorkspaceApi : IPaperTodoHostApi, IDisposable
{
    private readonly PaperBodyPluginHostApi _inner;

    public PaperAppRuntimeWorkspaceApi(
        AppController controller,
        string providerId,
        IEnumerable<string> permissions,
        Func<bool> isActive)
    {
        _inner = new PaperBodyPluginHostApi(
            controller,
            controller.PaperCommands,
            hostPaperId: string.Empty,
            providerId,
            permissions,
            isSessionCurrent: isActive,
            canReceiveEvents: isActive);
    }

    public IReadOnlySet<string> GrantedPermissions => _inner.GrantedPermissions;
    public IReadOnlyList<PaperSnapshot> ListPapers(string? type = null) => _inner.ListPapers(type);
    public PaperSnapshot? GetPaper(string paperId) => _inner.GetPaper(paperId);
    public IReadOnlyList<TodoSnapshot> ListTodos(string? paperId = null, bool includeBlank = false) =>
        _inner.ListTodos(paperId, includeBlank);
    public NoteSnapshot? GetNote(string paperId) => _inner.GetNote(paperId);
    public PaperMutationResult CreatePaper(CreatePaperRequest request) => _inner.CreatePaper(request);
    public AppendTodosResult AppendTodos(AppendTodosRequest request) => _inner.AppendTodos(request);
    public TodoMutationResult UpdateTodo(UpdateTodoRequest request) => _inner.UpdateTodo(request);
    public TodoMutationResult SetTodoReminder(SetTodoReminderRequest request) =>
        _inner.SetTodoReminder(request);
    public NoteMutationResult WriteNote(WriteNoteRequest request) => _inner.WriteNote(request);
    public DeleteMutationResult DeleteTodo(DeleteTodoRequest request) => _inner.DeleteTodo(request);
    public DeleteMutationResult DeletePaper(string paperId) => _inner.DeletePaper(paperId);
    public IDisposable Subscribe(PaperTodoEventFilter filter, Action<PaperTodoEvent> handler) =>
        _inner.Subscribe(filter, handler);
    public void Dispose() => _inner.Dispose();
}

internal sealed class PaperAppRuntimeGlobalTopBarApi : IPaperGlobalTopBarApi, IDisposable
{
    private readonly AppController _controller;
    private readonly Guid _runtimeId;
    private readonly string _providerId;
    private readonly Func<bool> _isActive;
    private Action<PaperTopBarActionInvocation>? _handler;
    private bool _disposed;

    public PaperAppRuntimeGlobalTopBarApi(
        AppController controller,
        Guid runtimeId,
        string providerId,
        Func<bool> isActive)
    {
        _controller = controller;
        _runtimeId = runtimeId;
        _providerId = providerId;
        _isActive = isActive;
    }

    public void SetActionHandler(Action<PaperTopBarActionInvocation>? handler)
    {
        EnsureUsable();
        _handler = handler;
        if (handler == null)
        {
            _controller.RemovePluginGlobalTopBarRuntime(_runtimeId, _providerId);
        }
    }

    public void SetActions(IReadOnlyList<PaperTopBarAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        EnsureUsable();
        if (actions.Count > 0 && _handler == null)
        {
            throw new PaperTodoPluginException(
                "topbar_handler_missing",
                "Register a global top-bar action handler before contributing actions.");
        }
        _controller.SetPluginGlobalTopBarActions(
            _runtimeId,
            _providerId,
            actions,
            () => !_disposed && _isActive(),
            Dispatch);
    }

    public void Clear()
    {
        EnsureUsable();
        _handler = null;
        _controller.RemovePluginGlobalTopBarRuntime(_runtimeId, _providerId);
    }

    private void Dispatch(PaperTopBarActionInvocation invocation)
    {
        if (_disposed || !_isActive())
        {
            return;
        }
        _handler?.Invoke(invocation);
    }

    private void EnsureUsable()
    {
        if (_disposed || !_isActive())
        {
            throw new PaperTodoPluginException(
                "runtime_closed",
                "The plugin app runtime is no longer active.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _handler = null;
        try { _controller.RemovePluginGlobalTopBarRuntime(_runtimeId, _providerId); } catch { }
    }
}
