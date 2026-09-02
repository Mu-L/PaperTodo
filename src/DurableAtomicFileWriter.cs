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

internal interface IDurableAtomicFileIo
{
    void WriteAndFlush(string path, byte[] bytes);
    void Replace(string tempPath, string targetPath);
    void Delay(TimeSpan delay);
    void Delete(string path);
}

internal sealed class DurableAtomicFileWriter : IDurableAtomicFileWriter
{
    private const int ReplaceAttemptCount = 5;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(100);

    private sealed class SystemFileIo : IDurableAtomicFileIo
    {
        public void WriteAndFlush(string path, byte[] bytes)
        {
            using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        public void Replace(string tempPath, string targetPath) =>
            File.Move(tempPath, targetPath, overwrite: true);

        public void Delay(TimeSpan delay) =>
            Thread.Sleep(delay);

        public void Delete(string path) =>
            File.Delete(path);
    }

    private static readonly IDurableAtomicFileIo SystemIo = new SystemFileIo();

    private readonly Action<DurableAtomicWriteStage, string>? _faultInjector;
    private readonly IDurableAtomicFileIo _fileIo;

    internal static IDurableAtomicFileWriter Shared { get; } =
        new DurableAtomicFileWriter();

    internal DurableAtomicFileWriter(
        Action<DurableAtomicWriteStage, string>? faultInjector = null,
        IDurableAtomicFileIo? fileIo = null)
    {
        _faultInjector = faultInjector;
        _fileIo = fileIo ?? SystemIo;
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

        _fileIo.WriteAndFlush(tempPath, bytes);
        _faultInjector?.Invoke(DurableAtomicWriteStage.AfterTempWrite, targetPath);
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

    private void ReplaceWithRetry(string tempPath, string targetPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _fileIo.Replace(tempPath, targetPath);
                return;
            }
            catch (Exception ex) when (
                attempt < ReplaceAttemptCount &&
                IsRetryableReplaceFailure(ex))
            {
                _fileIo.Delay(ReplaceRetryDelay);
            }
        }
    }

    private static bool IsRetryableReplaceFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private void TryDelete(string path)
    {
        try
        {
            _fileIo.Delete(path);
        }
        catch
        {
            // A failed validation must never replace the old target. Temp cleanup is best-effort.
        }
    }
}
