namespace PaperTodo.Plugin;

/// <summary>
/// App-scoped global top-bar capability. Unlike PaperBodyContext.TopBar, this lifetime belongs to
/// the provider runtime rather than any one paper/body session. PaperTodo removes all actions when
/// that provider runtime ends.
/// </summary>
public interface IPaperGlobalTopBarApi
{
    void SetActionHandler(Action<PaperTopBarActionInvocation>? handler);
    void SetActions(IReadOnlyList<PaperTopBarAction> actions);
    void Clear();
}

/// <summary>
/// Read-only view of the current host-managed settings for one provider app runtime. Json is read
/// on demand, so a long-lived runtime sees the latest normalized values without borrowing state
/// from any paper/body session.
/// </summary>
public interface IPaperAppRuntimeSettings
{
    string Json { get; }
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
/// Context for one provider-level runtime. The runtime exists while PaperTodo has at least one real
/// Note paper whose BodyProviderId is this plugin. It does not depend on that paper being visible,
/// expanded, or having a live body session.
///
/// Native app runtimes may call Workspace / Settings / GlobalTopBar / GlobalShortcuts from worker
/// threads; PaperTodo marshals host operations to its UI dispatcher when required and keeps the
/// settings store internally synchronized. Those calls are synchronous from the plugin's point of
/// view. A runtime therefore must not block its Dispose implementation waiting for a worker that can
/// itself be blocked inside one of these host calls, otherwise the UI thread and worker can deadlock
/// during shutdown. Keep host calls short and make worker shutdown cancellation-based rather than
/// UI-thread join-based.
/// </summary>
public sealed class PaperAppRuntimeContext
{
    public required string ProviderId { get; init; }
    public required string ApiVersion { get; init; }
    public required IReadOnlySet<string> GrantedPermissions { get; init; }
    public required IPaperTodoHostApi Workspace { get; init; }
    public required IPaperAppRuntimeSettings Settings { get; init; }
    public required IPaperGlobalTopBarApi GlobalTopBar { get; init; }
    public required IPaperGlobalShortcutApi GlobalShortcuts { get; init; }
}

/// <summary>
/// Optional protocol-2.0 Native capability. A plugin declaring the manifest capability
/// "appRuntime" must implement this interface. After startupPaper handling, PaperTodo starts one
/// provider runtime when at least one real paper uses the provider; a later 0 -> 1 transition starts
/// it as well, and deleting/repurposing the last such paper ends it.
/// </summary>
public interface IPaperAppRuntimeProvider
{
    IPaperAppRuntime CreateAppRuntime(PaperAppRuntimeContext context);
}

/// <summary>
/// One provider-level plugin runtime. It is not a hidden paper session and owns no Paper/Body/Mini
/// presentation. Dispose ends the runtime and revokes its provider-level contributions. Native
/// implementations are disposed from PaperTodo's UI-owned runtime lifecycle; Dispose must return
/// promptly and must not synchronously join workers that may call back into PaperTodo host APIs.
/// </summary>
public interface IPaperAppRuntime : IDisposable
{
}
