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

public enum PaperCapsuleComponentKind
{
    Text,
    Glyph,
    StatusDot,
    ProgressRing,
    ProgressBar
}

public enum PaperCapsuleTone
{
    Default,
    Muted,
    Accent,
    Warning,
    Danger
}

/// <summary>
/// One host-rendered item inside the fixed-height capsule content area. Up to three items are
/// accepted and their order is preserved. Fill consumes remaining horizontal space.
/// </summary>
public sealed record PaperCapsuleComponent
{
    public PaperCapsuleComponentKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public double Value { get; init; }
    public double Width { get; init; }
    public bool Fill { get; init; }
    public PaperCapsuleTone Tone { get; init; }
    public string Color { get; init; } = string.Empty;
}

/// <summary>
/// Protocol 1.6 host-rendered capsule description. A positive PreferredWidth is the complete
/// capsule content segment width in DIPs. AutomaticWidth asks PaperTodo to measure the natural
/// width of the standard components, including their internal padding and gaps. PaperTodo owns the
/// fixed height, outer chrome, close segment and all input.
/// </summary>
public sealed record PaperCapsulePresentation
{
    public const double AutomaticWidth = 0;

    public PaperCapsuleComponent[] Components { get; init; } = [];
    public double PreferredWidth { get; init; } = 110;
    public string ToolTip { get; init; } = string.Empty;
    public string PlainText { get; init; } = string.Empty;
}

public enum PaperCapsuleSurfaceKind
{
    Regular,
    Docked
}

/// <summary>
/// Geometry and theme of one fixed-height capsule content surface. Width and Height are the exact
/// custom-view layout slot in DIPs: protocol 1.7 does not subtract the protocol 1.6 template's
/// visual padding. The host keeps ownership of outer chrome and input.
/// </summary>
public sealed record PaperCapsuleViewContext(
    PaperCapsuleSurfaceKind Surface,
    double Width,
    double Height,
    PaperBodyTheme Theme);

/// <summary>
/// Optional protocol 1.7 native-session capability. A session may create one fresh WPF view for
/// each live capsule surface. The host attempts each surface at most once per live body session and
/// caches either the returned view or a null fallback. AutomaticWidth resolves from the standard
/// component template before this method is called. Any resolved-width geometry change recreates
/// the surface with a new context; presentation, theme and DPI changes that keep the same resolved
/// width reuse the view, so plugins that render live state should retain and update it through the
/// session lifecycle.
/// Returning null falls back to the protocol 1.6 host template.
/// </summary>
public interface IPaperCapsuleViewProvider
{
    FrameworkElement? CreateCapsuleView(PaperCapsuleViewContext context);
}

/// <summary>
/// Preferred complete edge mini-card size in device-independent pixels. The size includes the
/// host-owned chrome and close segment; PaperTodo normalizes it to the current monitor work area.
/// Protocol 1.8 accepts sizes from 120 x 90 DIPs through 480 x 420 DIPs.
/// </summary>
public readonly record struct PaperMiniViewSize(double Width, double Height)
{
    public static PaperMiniViewSize Default => new(320, 220);
}

/// <summary>
/// Exact geometry and theme supplied when PaperTodo creates a protocol 1.8 native mini view.
/// CardWidth/CardHeight describe the complete visible card. Width/Height describe the inner slot
/// owned by the plugin after the host chrome and close segment have been reserved.
/// </summary>
public sealed record PaperMiniViewContext(
    double CardWidth,
    double CardHeight,
    double Width,
    double Height,
    PaperBodyTheme Theme);

/// <summary>
/// Optional protocol 1.8 native-session capability for a dedicated edge-browsing surface. The
/// session and mini view may share one business-state model, but CreateMiniView must return a
/// fresh pure-WPF tree. Window, HwndHost, WindowsFormsHost, WebView2 and already-parented controls
/// are rejected. PaperTodo caches one successful view per live session and normalized geometry.
/// Returning null or throwing falls back to the enlarged protocol 1.7/1.6 capsule.
/// </summary>
public interface IPaperMiniViewProvider
{
    PaperMiniViewSize PreferredMiniViewSize => PaperMiniViewSize.Default;

    FrameworkElement? CreateMiniView(PaperMiniViewContext context);

    /// <summary>
    /// Notifies a cached mini tree when it becomes the active preview or starts leaving it.
    /// Plugins can pause timers and input work when hidden, but must keep the last painted tree
    /// intact for the host-owned outgoing animation. Business-state updates should continue
    /// according to the normal body-session visibility contract.
    /// </summary>
    void OnMiniViewVisibilityChanged(bool visible) { }
}

/// <summary>
/// Optional protocol 1.8 native-session opt-in for moving the one real body view into the first
/// mini preview before the body has ever been presented. PaperTodo owns reparenting and screenshot
/// hand-off. Only a pure-WPF body tree is eligible; unsupported surfaces fall back safely.
/// Dedicated IPaperMiniViewProvider content always takes precedence over migration.
/// </summary>
public interface IPaperBodyViewMigrationProvider
{
    PaperMiniViewSize PreferredMigratedMiniViewSize => new(360, 260);
}

/// <summary>
/// Marks a custom mini-view element as owning pointer input. Standard WPF buttons, selectors,
/// scroll bars, thumbs and hyperlinks are detected automatically. The edge host deliberately does
/// not take keyboard focus; text editing belongs in the full paper body.
/// </summary>
public static class PaperMiniViewInteraction
{
    public static readonly DependencyProperty ConsumesPointerProperty =
        DependencyProperty.RegisterAttached(
            "ConsumesPointer",
            typeof(bool),
            typeof(PaperMiniViewInteraction),
            new FrameworkPropertyMetadata(false));

    public static void SetConsumesPointer(DependencyObject element, bool value) =>
        element.SetValue(ConsumesPointerProperty, value);

    public static bool GetConsumesPointer(DependencyObject element) =>
        (bool)element.GetValue(ConsumesPointerProperty);
}

/// <summary>
/// Host-owned native controls. Plugins provide data and behavior while PaperTodo owns the
/// shared visual language, popup lifecycle, theme and DPI behavior.
/// </summary>
public interface IPaperBodyControls
{
    void ApplySelectStyle(ComboBox comboBox, double fontSize);
}

/// <summary>
/// Operations that belong to the paper carrying this plugin instance. The paper remains the
/// product-level anchor even when the plugin reads or mutates workspace data.
/// </summary>
public sealed class PaperBodyPaperContext
{
    public required string PaperId { get; init; }
    public required Action<string> SetTitle { get; init; }
    public required Action<string> SetHeaderText { get; init; }
    public required Action<PaperCapsulePresentation?> SetCapsulePresentation { get; init; }
}

/// <summary>
/// Operations that belong to the expanded body surface itself.
/// </summary>
public sealed class PaperBodySurfaceContext
{
    public required IPaperBodyControls Controls { get; init; }
    public required PaperBodyTheme Theme { get; init; }
    public required Action<PaperBodyInputClaims> SetInputClaims { get; init; }
    public required Action MarkDirty { get; init; }
    public required Action<string> OpenExternal { get; init; }
    public required Action RequestReload { get; init; }
}

/// <summary>
/// One plugin instance is anchored to one paper. Paper contains paper-owned presentation state,
/// Body contains the expanded body surface, and Workspace exposes PaperTodo-wide data operations.
/// Paper, Body and Workspace are the canonical capability scopes.
/// </summary>
public sealed class PaperBodyContext
{
    public required string ProviderId { get; init; }
    public required string ApiVersion { get; init; }
    public required string StateJson { get; init; }
    public required int StateVersion { get; init; }
    public required int TargetStateVersion { get; init; }
    public string SettingsJson { get; init; } = "{}";
    public IReadOnlySet<string> GrantedPermissions { get; init; } =
        PaperTodoPermissionNames.None;

    public required PaperBodyPaperContext Paper { get; init; }
    public required PaperBodySurfaceContext Body { get; init; }
    public required IPaperTodoHostApi Workspace { get; init; }
    public required Action<string> SaveStateJson { get; init; }

    // Convenience views for non-ambiguous values. Presentation writes stay in Paper / Body.
    public string PaperId => Paper.PaperId;
    public IPaperTodoHostApi Host => Workspace;
    public IPaperBodyControls Controls => Body.Controls;
    public PaperBodyTheme Theme => Body.Theme;
    public Action<string> SetTitle => Paper.SetTitle;
    public Action<PaperBodyInputClaims> SetInputClaims => Body.SetInputClaims;
    public Action MarkDirty => Body.MarkDirty;
    public Action<string> OpenExternal => Body.OpenExternal;
    public Action RequestReload => Body.RequestReload;
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

    // Whether the paper/plugin remains available at all. A visible capsule keeps this true even
    // while its full body is folded away.
    void OnVisibilityChanged(bool visible) { }

    // Whether the full paper body is currently presented and interactive.
    void OnPresentationChanged(bool visible) { }
    void OnThemeChanged(PaperBodyTheme theme) { }
    void OnTypographyChanged(PaperBodyTheme theme) { }
    void OnDpiChanged() { }

    // Host-rendered global settings changed for this plugin.
    void OnSettingsChanged(string settingsJson) { }
}
