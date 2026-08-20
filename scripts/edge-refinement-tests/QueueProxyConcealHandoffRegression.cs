using System.IO;

internal static class QueueProxyConcealHandoffRegression
{
    public static void Run()
    {
        var root = RepositoryRoot();
        foreach (var obsolete in new[]
        {
            "EdgeCapsuleProxySnapshotHost.cs",
            "EdgeCapsuleQueueCompositionProxy.DeferredHandoff.cs",
            "AppController.EdgeCapsulePointerComposition.cs",
            "PaperWindow.EdgeCapsulePointerComposition.cs",
            "AppController.EdgeCapsuleQueueProxyHandoff.cs"
        })
        {
            Require(
                !File.Exists(Path.Combine(root, obsolete)),
                $"{obsolete} must not exist in V3 Lite");
        }

        var policy = File.ReadAllText(
            Path.Combine(
                root,
                "EdgeCapsuleQueueProxyPolicy.cs"));
        Require(
            !policy.Contains("RevealTarget") &&
            !policy.Contains("ConcealSource") &&
            !policy.Contains("Snapshot"),
            "policy must expose only MovingSource");
    }

    private static string RepositoryRoot()
    {
        var directory =
            new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(
                   directory.FullName,
                   "PaperTodo.csproj")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ??
            throw new InvalidOperationException(
                "repository root not found");
    }

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
