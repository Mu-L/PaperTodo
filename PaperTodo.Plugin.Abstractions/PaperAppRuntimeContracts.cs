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
/// One host-managed shortcut invocation routed to the plugin app runtime. SettingId names the
/// manifest setting and ActionId is that setting's shortcutAction value.
/// </summary>
public sealed record PaperShortcutActionInvocation(
    string SettingId,
    string ActionId);

/// <summary>
/// App-scoped callback endpoint for plugin-defined global shortcut actions. PaperTodo owns the
/// actual Windows hotkey registration, conflict handling, persistence and settings UI. Host-owned
/// paper.* shortcut actions are executed directly by PaperTodo and are not sent to this handler.
/// </summary>
public interface IPaperGlobalShortcutApi
{
    void SetActionHandler(Action<PaperShortcutActionInvocation>? handler);
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
    public required IPaperGlobalShortcutApi GlobalShortcuts { get; init; }
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
/// Mini presentation. Dispose ends the runtime and revokes its process-level contributions.
/// </summary>
public interface IPaperAppRuntime : IDisposable
{
}
