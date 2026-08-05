using System.Windows;
using System.Windows.Controls;

namespace PaperTodo.Plugin;

[Flags]
public enum PaperBodyCapabilities
{
    None = 0,
    TextZoom = 1 << 0,
    NoteLinks = 1 << 1
}

[Flags]
public enum PaperBodyInputClaims
{
    None = 0,
    EscapeKey = 1 << 0,
    ContextMenu = 1 << 1
}

[Flags]
public enum PaperBodyRuntimeRequirements
{
    None = 0,
    BackgroundUpdates = 1 << 0
}

public sealed record PaperBodyTheme(
    bool IsDark,
    string PaperColor,
    string TextColor,
    string WeakTextColor,
    string AccentColor,
    string BorderColor,
    string FontFamily,
    double FontScale);

/// <summary>
/// Host-owned native controls. Plugins provide data and behavior while PaperTodo owns the
/// shared visual language, popup lifecycle, theme and DPI behavior.
/// </summary>
public interface IPaperBodyControls
{
    void ApplySelectStyle(ComboBox comboBox, double fontSize);
}

/// <summary>
/// Narrow host surface exposed to fully trusted, unsandboxed PaperTodo body plugins.
/// Callbacks are queued onto PaperTodo's UI dispatcher and ignored after the session is replaced.
/// </summary>
public sealed class PaperBodyContext
{
    public required string PaperId { get; init; }
    public required string ProviderId { get; init; }
    public required string ApiVersion { get; init; }
    public required string StateJson { get; init; }
    public required int StateVersion { get; init; }
    public required int TargetStateVersion { get; init; }
    public string SettingsJson { get; init; } = "{}";
    public IReadOnlySet<string> GrantedPermissions { get; init; } =
        PaperTodoPermissionNames.None;
    public required IPaperTodoHostApi Host { get; init; }
    public required IPaperBodyControls Controls { get; init; }
    public required PaperBodyTheme Theme { get; init; }
    public required Action<string> SaveStateJson { get; init; }
    public required Action<string> SetTitle { get; init; }
    public required Action<string> SetDisplayTitle { get; init; }
    public required Action<PaperBodyInputClaims> SetInputClaims { get; init; }
    public required Action MarkDirty { get; init; }

    [Obsolete("Use SetDisplayTitle. Protocol 1.1 display titles apply to both paper and capsule.")]
    public void SetCapsuleText(string text) => SetDisplayTitle(text);
    public required Action<string> OpenExternal { get; init; }
    public required Action RequestReload { get; init; }
}

/// <summary>
/// A fully trusted, unsandboxed native plugin loaded from one self-contained
/// plugins/&lt;plugin-id&gt;/ folder with the current user's permissions.
/// Implementations must provide a public parameterless constructor and act as stateless factories.
/// PaperTodo creates a fresh plugin object for every body session.
/// </summary>
public interface IPaperBodyPlugin
{
    string Id { get; }
    string DisplayName { get; }
    string Description => string.Empty;
    Version Version => new(1, 0);
    string ApiVersion { get; }
    int StateVersion => 1;
    PaperBodyRuntimeRequirements RuntimeRequirements { get; }
    PaperBodyCapabilities Capabilities { get; }

    /// <summary>
    /// Migrate persisted JSON before Create is called. Return valid JSON for StateVersion.
    /// The default implementation keeps the old JSON unchanged.
    /// </summary>
    string MigrateState(string stateJson, int fromVersion) => stateJson;

    IPaperBodySession Create(PaperBodyContext context);
}

/// <summary>
/// One live body instance attached to one PaperTodo paper.
/// Web plugins must call papertodo.saveState after every state mutation; Commit is best-effort only.
/// </summary>
public interface IPaperBodySession : IDisposable
{
    FrameworkElement View { get; }

    void Commit() { }
    void RefreshFromModel() { }
    void CancelInteractions() { }
    void OnActivated() { }
    void OnDeactivated() { }

    // Protocol 1.1: whether the paper/plugin remains available at all. A visible capsule keeps
    // this true even while its full body is folded away.
    void OnVisibilityChanged(bool visible) { }

    // Protocol 1.1: whether the full paper body is currently presented and interactive.
    void OnPresentationChanged(bool visible) { }
    void OnThemeChanged(PaperBodyTheme theme) { }
    void OnTypographyChanged(PaperBodyTheme theme) { }
    void OnDpiChanged() { }

    // Protocol 1.2: host-rendered global settings changed for this plugin.
    void OnSettingsChanged(string settingsJson) { }
}
