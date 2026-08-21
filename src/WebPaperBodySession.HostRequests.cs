using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private object? ExecuteMiniHostRequest(string method, JsonElement parameters) =>
        method switch
        {
            "papers.list" or
            "papers.get" or
            "papers.create" or
            "papers.delete" or
            "todos.list" or
            "todos.append" or
            "todos.update" or
            "todos.setReminder" or
            "todos.delete" or
            "notes.get" or
            "notes.write" => ExecuteHostRequest(method, parameters),
            _ => throw new PaperTodoPluginException(
                "method_not_found",
                $"The Web mini surface cannot call host method: {method}")
        };
}
