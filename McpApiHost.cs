using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace PaperTodo;

internal sealed class McpApiHost : IDisposable
{
    public const string PipeName = "PaperTodo-Mcp-Api-v1";
    // 200 normal-size todo rows can exceed one million JSON characters after escaping.
    private const int MaxRequestCharacters = 8_000_000;

    private readonly Dispatcher _dispatcher;
    private readonly McpCommandService _commands;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;
    private bool _disposed;

    public McpApiHost(
        Dispatcher dispatcher,
        McpCommandService commands)
    {
        _dispatcher = dispatcher;
        _commands = commands;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _listenerTask ??= Task.Run(() => ListenAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(token);
                await ProcessConnectionAsync(server, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await DelayAfterFailureAsync(token);
            }
            catch (UnauthorizedAccessException)
            {
                await DelayAfterFailureAsync(token);
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // Keep the local interface alive after an isolated connection failure.
                await DelayAfterFailureAsync(token);
            }
        }
    }

    private static async Task DelayAfterFailureAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessConnectionAsync(
        Stream stream,
        CancellationToken token)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true
        };

        var line = await ReadRequestLineAsync(reader, token);
        object response = line == null
            ? ErrorResponse(
                null,
                "request_too_large",
                "The MCP request is too large.")
            : await DispatchAsync(line, token);

        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
    }

    private static async Task<string?> ReadRequestLineAsync(
        StreamReader reader,
        CancellationToken token)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer, token);
            if (read == 0)
            {
                return text.Length == 0 ? "" : text.ToString();
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    if (text.Length > 0 && text[^1] == '\r')
                    {
                        text.Length--;
                    }
                    return text.ToString();
                }

                text.Append(character);
                if (text.Length > MaxRequestCharacters)
                {
                    return null;
                }
            }
        }
    }

    private async Task<object> DispatchAsync(
        string json,
        CancellationToken token)
    {
        JsonElement? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var request = document.RootElement.Clone();
            if (request.ValueKind != JsonValueKind.Object)
            {
                throw new McpApiException(
                    "invalid_request",
                    "The request must be a JSON object.");
            }

            if (request.TryGetProperty("id", out var idElement))
            {
                requestId = idElement.Clone();
            }

            var operation = _dispatcher.InvokeAsync(
                () => _commands.Execute(request),
                DispatcherPriority.Normal,
                token);
            var result = await operation.Task;
            return new { id = requestId, ok = true, result };
        }
        catch (McpApiException ex)
        {
            return ErrorResponse(requestId, ex.Code, ex.Message);
        }
        catch (JsonException)
        {
            return ErrorResponse(
                requestId,
                "invalid_json",
                "The request is not valid JSON.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ErrorResponse(
                requestId,
                "internal_error",
                "PaperTodo could not complete the request.");
        }
    }

    private static object ErrorResponse(
        JsonElement? id,
        string code,
        string message)
        => new { id, ok = false, error = new { code, message } };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        var listener = _listenerTask;
        if (listener == null)
        {
            _cts.Dispose();
            return;
        }

        _ = listener.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                _cts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class McpApiException : Exception
{
    public McpApiException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
