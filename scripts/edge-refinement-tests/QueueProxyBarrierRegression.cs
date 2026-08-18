using System.IO;

internal static class QueueProxyBarrierRegression
{
    public static void Run()
    {
        var startup = Source("EdgeCapsuleQueueCompositionProxy.Startup.cs");
        var handoff = Source("EdgeCapsuleQueueCompositionProxy.Handoff.cs");
        Require(startup.Contains("_endpointCommitRequested(_animationStartedAtTimestamp)"),
            "real endpoint and WPF morph must share the DComp QPC start");
        Require(startup.Contains("mode=live-translation"),
            "translation-only startup marker missing");
        Require(!startup.Contains("RequiresStartSnapshot") &&
                !startup.Contains("targetSurfaces") &&
                !startup.Contains("RevealTarget") &&
                !startup.Contains("ConcealSource"),
            "V3 Lite startup must not regrow resize/snapshot handoff");
        Require(handoff.Contains("? ReleaseAfterCoverLoss()"),
            "rollback loss must restore visible real authority immediately");
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
