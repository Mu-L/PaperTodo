using System.Runtime.CompilerServices;

namespace PaperTodo;

/// <summary>
/// Source-level architecture checks for the queue compositor's native synchronization boundary.
/// These are deliberately kept beside the executable regressions: API-level unit tests cannot
/// observe a UI-thread DComp wait or distinguish one queue barrier from N per-window barriers.
/// </summary>
internal static class QueueProxyBarrierRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var repositoryRoot = RepositoryRoot();
        var startup = MaskTrivia(MethodBody(
            Read(repositoryRoot, "EdgeCapsuleQueueCompositionProxy.Startup.cs"),
            "private bool PrepareAndStart()"));
        var handoff = MaskTrivia(MethodBody(
            Read(repositoryRoot, "EdgeCapsuleQueueCompositionProxy.Handoff.cs"),
            "public bool TryReleaseForHandoff()"));
        var nativeBatch = MaskTrivia(MethodBody(
            Read(repositoryRoot, "WindowNative.cs"),
            "TrySetWindowCloakedBatch("));
        var closeForDrag = MaskTrivia(MethodBody(
            Read(repositoryRoot, "AppController.EdgeCapsulePreview.cs"),
            "internal bool CloseEdgeCapsulePreviewForDrag("));
        var runtimeFailure = MaskTrivia(MethodBody(
            Read(repositoryRoot, "EdgeCapsuleQueueCompositionProxy.Runtime.cs"),
            "private void InvalidateAndDrain("));
        var sharedRuntimeLoss = MaskTrivia(MethodBody(
            Read(repositoryRoot, "EdgeCapsuleQueueCompositionProxy.Routing.cs"),
            "private void HandleSharedRuntimeLost()"));

        Assert(
            Count(startup, "WaitForCommitCompletion") == 1,
            "startup must retain exactly the initial root-publication wait; member cloak must not add another wait");
        Assert(
            startup.Contains("TrySetWindowCloakedBatch", StringComparison.Ordinal),
            "startup must cloak the queue through WindowNative.TrySetWindowCloakedBatch");
        Assert(
            Count(startup, "new WindowNative.WindowCloakChange") >= 2 &&
            startup.Contains("SnapshotHost", StringComparison.Ordinal) &&
            startup.IndexOf("TrySetWindowCloakedBatch", StringComparison.Ordinal) >
                startup.LastIndexOf(
                    "new WindowNative.WindowCloakChange",
                    StringComparison.Ordinal),
            "startup must collect both real and snapshot HWNDs before submitting one cloak batch");
        Assert(
            !startup.Contains("TrySetWindowCloaked(", StringComparison.Ordinal) &&
            !startup.Contains(".TrySetCloaked(", StringComparison.Ordinal),
            "startup must not cloak real or snapshot HWNDs one member at a time");
        AssertNoBarrierInsideMemberLoops(startup, "startup cloak");

        Assert(
            handoff.Contains("TrySetWindowCloakedBatch", StringComparison.Ordinal),
            "successful handoff must release the queue through one native cloak batch");
        Assert(
            !handoff.Contains("TrySetWindowCloaked(", StringComparison.Ordinal),
            "successful handoff and rollback must not toggle cloak one HWND at a time");
        Assert(
            Count(handoff, "WaitForCommitCompletion") == 0,
            "successful handoff detach must never synchronously wait for a DComp commit");
        var revealBatchIndex = handoff.IndexOf(
            "TrySetWindowCloakedBatch",
            StringComparison.Ordinal);
        var detachIndex = handoff.IndexOf(
            "_target.SetRoot(null!)",
            StringComparison.Ordinal);
        Assert(
            handoff.IndexOf("Cloaked: false", StringComparison.Ordinal) >= 0 &&
            revealBatchIndex >= 0 &&
            revealBatchIndex < detachIndex &&
            detachIndex < handoff.IndexOf(
                "_device.Commit()",
                detachIndex,
                StringComparison.Ordinal),
            "handoff must reveal and verify the whole real queue before committing proxy detach");
        var successfulDetach = handoff[
            RequiredIndexOf(
                handoff,
                "_sourcesReleased = true;",
                "successful handoff release marker")..];
        Assert(
            Count(successfulDetach, "FlushDesktopComposition") <= 1,
            "successful handoff may publish at most one queue-wide desktop barrier");
        AssertNoBarrierInsideMemberLoops(handoff, "successful handoff");
        var releaseFailure = handoff[
            RequiredIndexOf(
                handoff,
                "catch (Exception ex)",
                "handoff cleanup failure branch")..];
        Assert(
            !releaseFailure.Contains("Cloaked: true", StringComparison.Ordinal) &&
            !releaseFailure.Contains("RollbackCloaked: false", StringComparison.Ordinal),
            "a cleanup failure after verified reveal must never re-cloak real endpoints");

        var successfulBatch = nativeBatch[..RequiredIndexOf(
            nativeBatch,
            "if (!verified)",
            "cloak batch rollback branch")];
        Assert(
            Count(successfulBatch, "FlushDesktopComposition") +
                Count(successfulBatch, "DwmFlush(") == 1,
            "the successful native cloak batch must perform exactly one desktop flush boundary");
        Assert(
            BarrierIndex(successfulBatch) > successfulBatch.IndexOf(
                "TrySetWindowCloakAttribute",
                StringComparison.Ordinal),
            "the cloak batch barrier must occur after its cloak writes");
        Assert(
            successfulBatch.IndexOf(
                "TryGetWindowAppCloaked",
                StringComparison.Ordinal) > BarrierIndex(successfulBatch),
            "the cloak batch must verify every requested state only after the shared barrier");

        Assert(
            closeForDrag.Contains(
                "CloseEdgeCapsulePreview(animate: true, arrange: true)",
                StringComparison.Ordinal),
            "drag threshold must stage an animated preview conceal transaction");
        AssertNoSynchronousDragFlush(closeForDrag, "preview close for drag");

        Assert(
            runtimeFailure.Contains("HandleSharedRuntimeLost", StringComparison.Ordinal) &&
            runtimeFailure.Contains("RetireInvalidHostIfIdle", StringComparison.Ordinal) &&
            !runtimeFailure.Contains("_hosts.Clear", StringComparison.Ordinal),
            "one failed DComp target must drain active queues before the shared device is destroyed");
        Assert(
            sharedRuntimeLoss.IndexOf("_coverLost = true", StringComparison.Ordinal) <
                sharedRuntimeLoss.IndexOf("BeginInvoke", StringComparison.Ordinal) &&
            sharedRuntimeLoss.Contains("CompleteNow(success: false)", StringComparison.Ordinal),
            "an affected queue must stop routing immediately and schedule its own safe source reveal");
    }

    private static void AssertNoBarrierInsideMemberLoops(
        string method,
        string scenario)
    {
        foreach (var loop in Blocks(method, "foreach"))
        {
            Assert(
                !loop.Contains("FlushDesktopComposition", StringComparison.Ordinal) &&
                !loop.Contains("WaitForCommitCompletion", StringComparison.Ordinal),
                $"{scenario} must not put a desktop/DComp barrier inside a per-member loop");
        }
    }

    private static void AssertNoSynchronousDragFlush(
        string method,
        string scenario)
    {
        foreach (var forbidden in new[]
                 {
                     "FlushEdgeCapsulePreviewCompactPresentation",
                     "FlushEdgeCapsulePresentation",
                     "FlushDesktopComposition",
                     "DwmFlush(",
                     "WaitForCommitCompletion",
                     "Dispatcher.Invoke(",
                     ".Wait("
                 })
        {
            Assert(
                !method.Contains(forbidden, StringComparison.Ordinal),
                $"{scenario} must not synchronously consume the staged queue transition: {forbidden}");
        }
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath));

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
            var directory = string.IsNullOrWhiteSpace(seed)
                ? null
                : new DirectoryInfo(seed);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "EdgeCapsuleQueueCompositionProxy.Startup.cs")) &&
                    File.Exists(Path.Combine(
                        directory.FullName,
                        "WindowNative.cs")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
        }
        throw new InvalidOperationException(
            "cannot locate the PaperTodo source root for queue barrier regressions");
    }

    private static string MethodBody(string source, string signature)
    {
        var masked = MaskTrivia(source);
        var signatureIndex = masked.IndexOf(signature, StringComparison.Ordinal);
        Assert(signatureIndex >= 0, $"source method not found: {signature}");
        var open = masked.IndexOf('{', signatureIndex + signature.Length);
        Assert(open >= 0, $"source method has no body: {signature}");
        var close = MatchingBrace(masked, open);
        Assert(close > open, $"source method body is unbalanced: {signature}");
        return source[open..(close + 1)];
    }

    private static IEnumerable<string> Blocks(string source, string keyword)
    {
        var masked = MaskTrivia(source);
        var search = 0;
        while (search < masked.Length)
        {
            var keywordIndex = masked.IndexOf(keyword, search, StringComparison.Ordinal);
            if (keywordIndex < 0)
            {
                yield break;
            }
            search = keywordIndex + keyword.Length;
            if ((keywordIndex > 0 && IsIdentifier(masked[keywordIndex - 1])) ||
                (search < masked.Length && IsIdentifier(masked[search])))
            {
                continue;
            }

            var open = masked.IndexOf('{', search);
            if (open < 0)
            {
                yield break;
            }
            var close = MatchingBrace(masked, open);
            if (close < 0)
            {
                yield break;
            }
            yield return source[open..(close + 1)];
            search = close + 1;
        }
    }

    private static int MatchingBrace(string masked, int open)
    {
        var depth = 0;
        for (var index = open; index < masked.Length; index++)
        {
            if (masked[index] == '{')
            {
                depth++;
            }
            else if (masked[index] == '}' && --depth == 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static string MaskTrivia(string source)
    {
        var result = source.ToCharArray();
        for (var index = 0; index < result.Length; index++)
        {
            if (result[index] == '/' &&
                index + 1 < result.Length &&
                result[index + 1] == '/')
            {
                result[index++] = ' ';
                while (index + 1 < result.Length &&
                       result[index + 1] is not ('\r' or '\n'))
                {
                    result[++index] = ' ';
                }
            }
            else if (result[index] == '/' &&
                     index + 1 < result.Length &&
                     result[index + 1] == '*')
            {
                result[index++] = ' ';
                while (index + 1 < result.Length)
                {
                    if (result[index] == '*' && result[index + 1] == '/')
                    {
                        result[index] = result[index + 1] = ' ';
                        index++;
                        break;
                    }
                    if (result[index] is not ('\r' or '\n'))
                    {
                        result[index] = ' ';
                    }
                    index++;
                }
            }
            else if (result[index] is '"' or '\'')
            {
                var quote = result[index];
                var verbatim = quote == '"' && index > 0 && result[index - 1] == '@';
                result[index] = ' ';
                while (++index < result.Length)
                {
                    if (result[index] is '\r' or '\n')
                    {
                        continue;
                    }
                    if (!verbatim && result[index] == '\\')
                    {
                        result[index] = ' ';
                        if (index + 1 < result.Length)
                        {
                            result[++index] = ' ';
                        }
                        continue;
                    }
                    if (result[index] == quote)
                    {
                        if (verbatim && index + 1 < result.Length &&
                            result[index + 1] == quote)
                        {
                            result[index] = result[++index] = ' ';
                            continue;
                        }
                        result[index] = ' ';
                        break;
                    }
                    result[index] = ' ';
                }
            }
        }
        return new string(result);
    }

    private static bool IsIdentifier(char value) =>
        char.IsLetterOrDigit(value) || value == '_';

    private static int Count(string source, string value)
    {
        var count = 0;
        var search = 0;
        while ((search = source.IndexOf(
                   value,
                   search,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            search += value.Length;
        }
        return count;
    }

    private static int RequiredIndexOf(
        string source,
        string value,
        string description)
    {
        var index = source.IndexOf(value, StringComparison.Ordinal);
        Assert(index >= 0, $"source marker not found: {description}");
        return index;
    }

    private static int BarrierIndex(string source) => Math.Max(
        source.IndexOf("FlushDesktopComposition", StringComparison.Ordinal),
        source.IndexOf("DwmFlush(", StringComparison.Ordinal));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
