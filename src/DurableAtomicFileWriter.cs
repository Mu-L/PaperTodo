using System.IO;
using System.Threading;

namespace PaperTodo;

internal enum DurableAtomicWriteStage
{
    BeforeTempOpen,
    AfterTempWrite,
    AfterFlush,
    BeforeReplace
}

internal interface IDurableAtomicFileWriter
{
    void Write(
        string targetPath,
        byte[] bytes,
        Func<string, bool>? validateTemp = null);
}

internal sealed class DurableAtomicFileWriter : IDurableAtomicFileWriter
{
    private const int ReplaceAttemptCount = 5;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly Action<DurableAtomicWriteStage, string>? _faultInjector;

    internal static IDurableAtomicFileWriter Shared { get; } =
        new DurableAtomicFileWriter();

    internal DurableAtomicFileWriter(
        Action<DurableAtomicWriteStage, string>? faultInjector = null)
    {
        _faultInjector = faultInjector;
    }

    public void Write(
        string targetPath,
        byte[] bytes,
        Func<string, bool>? validateTemp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(bytes);

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = targetPath + ".tmp";
        _faultInjector?.Invoke(DurableAtomicWriteStage.BeforeTempOpen, targetPath);

        using (var stream = new FileStream(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 16 * 1024,
                   FileOptions.SequentialScan))
        {
            stream.Write(bytes);
            _faultInjector?.Invoke(DurableAtomicWriteStage.AfterTempWrite, targetPath);
            stream.Flush(flushToDisk: true);
        }

        _faultInjector?.Invoke(DurableAtomicWriteStage.AfterFlush, targetPath);

        if (validateTemp != null)
        {
            try
            {
                if (!validateTemp(tempPath))
                {
                    throw new InvalidDataException(
                        $"Durable temp validation failed for '{Path.GetFileName(targetPath)}'.");
                }
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        _faultInjector?.Invoke(DurableAtomicWriteStage.BeforeReplace, targetPath);
        ReplaceWithRetry(tempPath, targetPath);
    }

    private static void ReplaceWithRetry(string tempPath, string targetPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                attempt < ReplaceAttemptCount &&
                IsRetryableReplaceFailure(ex))
            {
                Thread.Sleep(ReplaceRetryDelay);
            }
        }
    }

    private static bool IsRetryableReplaceFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A failed validation must never replace the old target. Temp cleanup is best-effort.
        }
    }
}
