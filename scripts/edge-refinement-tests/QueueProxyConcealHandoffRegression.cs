using System.IO;
using System.Runtime.CompilerServices;

namespace PaperTodo;

internal static class QueueProxyConcealHandoffRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var root = RepositoryRoot();
        var paper = File.ReadAllText(Path.Combine(
            root,
            "PaperWindow.EdgeCapsuleQueueProxy.cs"));
        var freeze = File.ReadAllText(Path.Combine(
            root,
            "EdgeCapsuleQueueCompositionProxy.DeferredHandoff.cs"));

        var applyMethod = Between(
            paper,
            "internal bool TryApplyLatestEdgeCapsuleQueueProxyEndpoint(",
            "internal bool VerifyEdgeCapsuleQueueProxyEndpoint(");
        var freezeCall = RequiredIndex(
            applyMethod,
            "TryFreezeEdgeCapsuleQueueProxyDeferredEndpointSource(this)");
        var endpointApply = RequiredIndex(
            applyMethod,
            "ApplyEdgeCapsuleQueueProxyEndpoint(endpoint)");
        Assert(
            freezeCall < endpointApply,
            "ConcealSource must be frozen before the real HWND can be resized to compact.");

        var capture = RequiredIndex(
            freeze,
            "CaptureEdgeCapsuleQueueProxySnapshot(");
        var sourceFrame = RequiredIndex(
            freeze,
            "member.Plan.Source);");
        var addFrozen = RequiredIndex(
            freeze,
            "frozenVisual = AddVisual(");
        var hideLive = RequiredIndex(
            freeze,
            "liveVisual.Effect.SetOpacity(0)");
        var commit = RequiredIndex(
            freeze,
            "_device.Commit().CheckError()");
        var flush = RequiredIndex(
            freeze,
            "WindowNative.TryFlushDesktopComposition()");
        var retainHost = RequiredIndex(
            freeze,
            "mutableMembers[memberIndex] = frozenMember");
        Assert(
            capture < sourceFrame &&
            sourceFrame < addFrozen &&
            addFrozen < hideLive &&
            hideLive < commit &&
            commit < flush &&
            flush < retainHost,
            "The full live preview must be replaced by a published frozen surface before endpoint mutation is allowed.");
        Assert(
            freeze.Contains(
                "state.Layer == EdgeCapsuleQueueProxyVisualLayer.ConcealSource",
                StringComparison.Ordinal) &&
            freeze.Contains(
                "if (!member.Plan.DefersRealEndpoint)",
                StringComparison.Ordinal),
            "Only deferred ConcealSource members should pay the handoff freeze cost.");
    }

    private static string Between(string source, string start, string end)
    {
        var startIndex = RequiredIndex(source, start);
        var endIndex = source.IndexOf(
            end,
            startIndex + start.Length,
            StringComparison.Ordinal);
        Assert(endIndex > startIndex, $"source marker not found: {end}");
        return source[startIndex..endIndex];
    }

    private static int RequiredIndex(string source, string value)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        Assert(index >= 0, $"source marker not found: {value}");
        return index;
    }

    private static string RepositoryRoot(
        [CallerFilePath] string sourcePath = "")
    {
        foreach (var seed in new[]
                 {
                     Path.GetDirectoryName(sourcePath),
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = string.IsNullOrWhiteSpace(seed)
                     ? null
                     : new DirectoryInfo(seed);
                 directory != null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "EdgeCapsuleQueueCompositionProxy.DeferredHandoff.cs")))
                {
                    return directory.FullName;
                }
            }
        }
        throw new InvalidOperationException("cannot locate the PaperTodo source root");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
