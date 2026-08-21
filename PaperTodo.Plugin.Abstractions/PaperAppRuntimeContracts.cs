namespace PaperTodo.Plugin;

/// <summary>
/// App-scoped global top-bar capability. Unlike PaperBodyContext.TopBar, this lifetime belongs to
/// the plugin runtime rather than any paper/body session. PaperTodo removes all actions when the
/// owning app runtime ends.
/// </summary>
public interface IPaperGlobalTopBarApi
{
    void SetActionHandler(Action<PaperTopBarActionInvocation>? handler);
    void SetActions(IReadOnlyList<PaperTopBarAction> actions);
    void Clear();
}

/// <summary>
/// Context for one plugin-level runtime. App runtimes start with PaperTodo and are independent of
/// whether any paper using the provider is open, visible, expanded, or even exists.
/// </summary>
public sealed class PaperAppRuntimeContext
{
    public required string ProviderId { get; init; }
    public required string ApiVersion { get; init; }
    public required IReadOnlySet<string> GrantedPermissions { get; init; }
    public required IPaperTodoHostApi Workspace { get; init; }
    public required IPaperGlobalTopBarApi GlobalTopBar { get; init; }
}

/// <summary>
/// Optional protocol-2.0 Native capability. A plugin declaring the manifest capability
/// "appRuntime" must implement this interface; PaperTodo loads it during application startup and
/// keeps the returned runtime alive until shutdown.
/// </summary>
public interface IPaperAppRuntimeProvider
{
    IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context);
}

/// <summary>
/// One application-level plugin runtime. It is not a hidden paper session and owns no Paper/Body/
/// Mini presentation. Dispose ends the runtime and revokes its global top-bar contribution.
/// </summary>
public interface IPaperAppRuntime : IDisposable
{
}
