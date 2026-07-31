using System.Windows;

namespace PaperTodo.Plugin;

[Flags]
public enum PaperBodyCapabilities
{
    None = 0,
    TextZoom = 1 << 0,
    NoteLinks = 1 << 1
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
/// Narrow host surface exposed to fully trusted, unsandboxed PaperTodo body plugins.
/// Callbacks are queued onto PaperTodo's UI dispatcher and ignored after the session is replaced.
/// </summary>
public sealed class PaperBodyContext
{
    public required string PaperId { get; init; }
    public required string ProviderId { get; init; }
    public required string StateJson { get; init; }
    public required int StateVersion { get; init; }
    public required int TargetStateVersion { get; init; }
    public required PaperBodyTheme Theme { get; init; }
    public required Action<string> SaveStateJson { get; init; }
    public required Action<string> SetTitle { get; init; }
    public required Action<string> SetCapsuleText { get; init; }
    public required Action MarkDirty { get; init; }
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
    int StateVersion => 1;
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
    void OnVisibilityChanged(bool visible) { }
    void OnThemeChanged(PaperBodyTheme theme) { }
    void OnTypographyChanged(PaperBodyTheme theme) { }
    void OnDpiChanged() { }
}
