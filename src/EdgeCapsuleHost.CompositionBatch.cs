using System.Diagnostics;

namespace PaperTodo;

internal sealed partial class EdgeCapsuleHost
{
    /// <summary>
    /// Drains this host's WPF layout without crossing the desktop-composition boundary. The queue
    /// compositor prepares every endpoint first, then performs one Render dispatch and one DWM
    /// flush for the complete queue instead of paying both barriers once per paper.
    /// </summary>
    internal bool PrepareCompositionSourceLayoutForBatchHandoff()
    {
        if (_disposed || !Window.IsVisible)
        {
            return false;
        }

        try
        {
            Window.UpdateLayout();
            VisualSurface.UpdateLayout();
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Edge capsule batched endpoint layout failed. Paper={0}; Exception={1}",
                _options.DiagnosticId,
                ex);
            return false;
        }
    }
}
