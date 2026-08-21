using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private object? ExecuteMiniHostRequest(string method, JsonElement parameters)
    {
        if (method.StartsWith("papers.", StringComparison.Ordinal) ||
            method.StartsWith("todos.", StringComparison.Ordinal) ||
            method.StartsWith("notes.", StringComparison.Ordinal))
        {
            return ExecuteHostRequest(method, parameters);
        }

        throw new PaperTodoPluginException(
            "method_not_found",
            $"The Web mini surface cannot call host method: {method}");
    }
}
