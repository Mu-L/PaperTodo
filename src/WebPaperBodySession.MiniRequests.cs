using System.Text.Json;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private object? ExecuteMiniHostRequest(string method, JsonElement parameters) =>
        WebPluginWorkspaceRequests.Execute(_context.Host, method, parameters);
}
