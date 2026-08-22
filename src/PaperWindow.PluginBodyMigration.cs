using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    // Protocol 1.8 exposed IPaperBodyViewMigrationProvider, so keep the ABI type available, but
    // the host no longer reparents the one authoritative body View into an edge preview. That
    // convenience path grew its own snapshot, warmup, retry and presentation lifecycle and made
    // the host responsible for plugin-specific visual-tree surgery. Dedicated IPaperMiniViewProvider
    // content remains the supported rich native mini path; otherwise the normal capsule fallback is
    // the final preview.
    private bool _pluginBodyEverPresented;

    private partial bool TryDescribeMigratedPluginBodyPreview(
        IPaperBodyViewMigrationProvider provider,
        EdgeCapsulePreviewContext context,
        out EdgeCapsulePreviewDescriptor descriptor)
    {
        descriptor = null!;
        return false;
    }

    private partial void ResetMigratedPluginBodyPreview()
    {
        _pluginBodyEverPresented = false;
    }

    // Kept as local no-ops while callers are shared with the normal body/edge lifecycle. They make
    // removal of the old migration machinery explicit without spreading compatibility branches
    // through unrelated placement, theme and body-session code.
    private void ScheduleMigratedPluginBodyPreviewWarmup()
    {
    }

    private void CaptureMigratedPluginBodyOnPointerLeave()
    {
    }

    private void RestorePrewarmedPluginBodyForActivation(string reason = "activation")
    {
    }
}
