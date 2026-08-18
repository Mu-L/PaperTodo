using System.IO;

internal static class QueueProxyConcealHandoffRegression
{
    public static void Run()
    {
        var policy = Source("EdgeCapsuleQueueProxyPolicy.cs");
        var startup = Source("EdgeCapsuleQueueCompositionProxy.Startup.cs");
        Require(policy.Contains("mode=translation-only"),
            "translation-only admission marker missing");
        Require(policy.Contains("FloatingCoverActive"),
            "visual authority must include floating cover lifetime");
        Require(startup.Contains("SnapshotHost: null") || !startup.Contains("SnapshotHost"),
            "normal startup must not allocate snapshot hosts");
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
