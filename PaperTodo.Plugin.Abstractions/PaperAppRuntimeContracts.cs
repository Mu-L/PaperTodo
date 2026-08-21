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
/// Context for one provider-level runtime. The runtime exists while PaperTodo has at least one real
/// Note paper whose BodyProviderId is this plugin. It does not depend on that paper being visible,
/// expanded, or having a live body session.
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
/// presentation. Dispose ends the runtime and revokes its global top-bar contribution.
/// </summary>
public interface IPaperAppRuntime : IDisposable
{
}
