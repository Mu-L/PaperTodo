using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private object SetPaperTopBarActionsFromWeb(JsonElement parameters)
    {
        var actions = ReadTopBarActions(parameters);
        var hidden = PaperHostTopBarActions.None;
        if (ReadStringSet(parameters, "hiddenHostActions") is { } hiddenValues)
        {
            foreach (var value in hiddenValues)
            {
                hidden |= value switch
                {
                    "newTodoPaper" => PaperHostTopBarActions.NewTodoPaper,
                    "newNotePaper" => PaperHostTopBarActions.NewNotePaper,
                    _ => throw new PaperTodoPluginException(
                        "invalid_topbar_host_action",
                        $"Unknown host top-bar action: {value}")
                };
            }
        }

        _context.Host.SetTopBarActionHandler(HandleTopBarActionInvocation);
        _context.Host.SetPaperTopBarActions(actions, hidden);
        return new { updated = actions.Length };
    }

    private object SetGlobalTopBarActionsFromWeb(JsonElement parameters)
    {
        var actions = ReadTopBarActions(parameters);
        _context.Host.SetTopBarActionHandler(HandleTopBarActionInvocation);
        _context.Host.SetGlobalTopBarActions(actions);
        return new { updated = actions.Length };
    }

    private static PaperTopBarAction[] ReadTopBarActions(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("actions", out var actionsValue) ||
            actionsValue.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (actionsValue.ValueKind != JsonValueKind.Array)
        {
            throw new PaperTodoPluginException(
                "invalid_params",
                "actions must be an array.");
        }
        return DeserializePayload<PaperTopBarAction[]>(actionsValue);
    }

    private void HandleTopBarActionInvocation(PaperTopBarActionInvocation invocation)
    {
        Send(new
        {
            type = "topBarActionInvoked",
            action = invocation
        });
    }
}
