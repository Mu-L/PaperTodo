using System.IO;

internal static class QueueProxyNativeClipRegression
{
    public static void Run()
    {
        var startup = Source("EdgeCapsuleQueueCompositionProxy.Startup.cs");
        var visuals = Source("EdgeCapsuleQueueCompositionProxy.Visuals.cs");
        Require(startup.Contains("FullClip(sourceHost)"),
            "translation proxy must expose the full stable live host");
        Require(visuals.Contains("layer == EdgeCapsuleQueueProxyVisualLayer.MovingSource"),
            "moving source must preserve the WPF-owned silhouette");
        Require(!startup.Contains("RoundedBodyClipForVisibleBounds"),
            "translation startup must not animate a resize clip");
    }

    private static string Source(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PaperTodo.csproj")))
        {
  directory = directory.Parent;
        }
        if (directory == null)
        {
  throw new InvalidOperationException("repository root not found");
        }
        return File.ReadAllText(Path.Combine(directory.FullName, name));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
  throw new InvalidOperationException(message);
        }
    }
}
