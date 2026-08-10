namespace PaperTodo;

public sealed partial class AppController
{
#if DEBUG
    private static readonly object EdgeCapsulePreviewTraceLock = new();
#endif

    private static string EdgeCapsulePreviewTraceId(string? paperId)
    {
        if (string.IsNullOrEmpty(paperId))
        {
            return "<none>";
        }

        return paperId[..Math.Min(6, paperId.Length)];
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void TraceEdgeCapsulePreview(string message)
    {
#if DEBUG
        try
        {
            var path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "edge-preview-trace.log");
            var line =
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (EdgeCapsulePreviewTraceLock)
            {
                System.IO.File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Debug-only diagnostics must never affect edge-preview interaction.
        }
#endif
    }
}
