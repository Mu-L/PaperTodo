using System.Globalization;
using System.Text.Json;

namespace PaperTodo;

internal sealed class McpCommandService
{
    private readonly AppController _controller;

    public McpCommandService(AppController controller)
    {
        _controller = controller;
    }

    public object? Execute(JsonElement request)
    {
        if (!_controller.IsRunning)
        {
            throw new McpApiException(
                "app_exiting",
                "PaperTodo is exiting.");
        }
        if (!_controller.State.McpEnabled)
        {
            throw new McpApiException(
                "mcp_disabled",
                "PaperTodo's MCP interface is disabled.");
        }

        var method = RequiredString(request, "method", 80);
        var parameters = request.TryGetProperty("params", out var value)
            ? RequireObject(value, "params")
            : default;

        _controller.CommitPendingNoteContentsForSave();
        return method switch
        {
            "list_papers" => ListPapers(parameters),
            "get_paper" => GetPaper(parameters),
            "create_todo_paper" => CreateTodoPaper(parameters),
            "create_note" => CreateNote(parameters),
            "add_todos" => AddTodos(parameters),
            "update_todo" => UpdateTodo(parameters),
            "set_todo_reminder" => SetTodoReminder(parameters),
            "write_note" => WriteNote(parameters),
            "delete_paper" => DeletePaper(parameters),
            "delete_todo" => DeleteTodo(parameters),
            _ => throw new McpApiException(
                "method_not_found",
                $"Unknown PaperTodo method: {method}")
        };
    }

    private object ListPapers(JsonElement parameters)
    {
        var type = OptionalString(parameters, "type", 20);
        if (type != null && type is not PaperTypes.Todo and not PaperTypes.Note)
        {
            throw new McpApiException(
                "invalid_params",
                "type must be 'todo' or 'note'.");
        }

        var papers = _controller.State.Papers
            .Where(paper => type == null || paper.Type == type)
            .Select(paper => new
            {
                id = paper.Id,
                type = paper.Type,
                title = _controller.PaperTitleText(paper),
                is_visible = paper.IsVisible,
                item_count = paper.Type == PaperTypes.Todo
                    ? paper.Items.Count
                    : 0,
                open_item_count = paper.Type == PaperTypes.Todo
                    ? paper.Items.Count(item =>
                        !item.Done &&
                        !string.IsNullOrWhiteSpace(item.Text))
                    : 0,
                content_length = paper.Type == PaperTypes.Note
                    ? paper.Content?.Length ?? 0
                    : 0
            })
            .ToArray();

        return new { papers };
    }

    private object GetPaper(JsonElement parameters)
        => PaperDetails(RequirePaper(parameters));

    private object CreateTodoPaper(JsonElement parameters)
    {
        RequireAdditiveWrites();
        EnsurePaperCapacity();
        var title = OptionalString(
            parameters,
            "title",
            _controller.State.MaxTitleLength);
        var show = OptionalBoolean(parameters, "show") ?? true;
        var inputs = ReadTodoInputs(parameters, required: false);
        RequireFullWritesForTodoMetadata(inputs);

        var paper = _controller.CreatePaper(
            PaperTypes.Todo,
            show: false)
            ?? throw new McpApiException(
                "paper_limit",
                "PaperTodo cannot create another paper.");
        paper.IsVisible = show;
        if (title != null)
        {
            paper.Title = PaperTitles.CleanCustomTitle(
                title,
                _controller.State.MaxTitleLength);
        }
        if (inputs.Count > 0)
        {
            paper.Items.Clear();
            AddTodoInputs(paper, inputs);
        }

        if (!_controller.TryCommitMcpMutation())
        {
            _controller.RollbackMcpCreatedPaper(paper);
            throw SaveFailed();
        }

        _controller.RunMcpPostCommitUi(
            () => _controller.FinalizeMcpPaperCreated(paper, show));
        return PaperDetails(paper);
    }

    private object CreateNote(JsonElement parameters)
    {
        RequireAdditiveWrites();
        EnsurePaperCapacity();
        var title = OptionalString(
            parameters,
            "title",
            _controller.State.MaxTitleLength);
        var content = OptionalString(
            parameters,
            "content",
            PaperWindow.NoteTextMaxLength,
            allowEmpty: true) ?? "";
        var show = OptionalBoolean(parameters, "show") ?? true;

        var paper = _controller.CreatePaper(
            PaperTypes.Note,
            show: false)
            ?? throw new McpApiException(
                "paper_limit",
                "PaperTodo cannot create another paper.");
        paper.IsVisible = show;
        if (title != null)
        {
            paper.Title = PaperTitles.CleanCustomTitle(
                title,
                _controller.State.MaxTitleLength);
        }
        paper.Content = content;

        if (!_controller.TryCommitMcpMutation())
        {
            _controller.RollbackMcpCreatedPaper(paper);
            throw SaveFailed();
        }

        _controller.RunMcpPostCommitUi(
            () => _controller.FinalizeMcpPaperCreated(paper, show));
        return PaperDetails(paper);
    }

    private object AddTodos(JsonElement parameters)
    {
        RequireAdditiveWrites();
        var paper = RequirePaper(parameters, PaperTypes.Todo);
        var inputs = ReadTodoInputs(parameters, required: true);
        RequireFullWritesForTodoMetadata(inputs);
        var snapshot = TodoPaperSnapshot.Capture(paper);
        var blankOnly = paper.Items.Count == 1 &&
            string.IsNullOrWhiteSpace(paper.Items[0].Text) &&
            !paper.Items[0].Done &&
            !paper.Items[0].ReminderAt.HasValue &&
            string.IsNullOrWhiteSpace(paper.Items[0].LinkedNoteId);
        if (blankOnly)
        {
            paper.Items.Clear();
        }

        var added = AddTodoInputs(paper, inputs);
        if (!_controller.TryCommitMcpMutation())
        {
            snapshot.Restore(paper);
            throw SaveFailed();
        }

        _controller.RunMcpPostCommitUi(
            () => _controller.RefreshMcpTodoPaper(paper));
        return new { paper_id = paper.Id, added };
    }

    private object UpdateTodo(JsonElement parameters)
    {
        var paper = RequirePaper(parameters, PaperTypes.Todo);
        var item = RequireTodo(parameters, paper);
        var hasText = parameters.TryGetProperty("text", out var textValue);
        var hasDone = parameters.TryGetProperty("done", out var doneValue);
        if (!hasText && !hasDone)
        {
            throw new McpApiException(
                "invalid_params",
                "Provide text and/or done to update the todo.");
        }

        string? text = null;
        if (hasText)
        {
            text = RequiredStringValue(
                textValue,
                "text",
                PaperWindow.TodoTextMaxLength,
                allowEmpty: true);
            if (string.IsNullOrWhiteSpace(item.Text))
            {
                RequireAdditiveWrites();
            }
            else if (!string.Equals(item.Text, text, StringComparison.Ordinal))
            {
                RequireFullWrites();
            }
        }
        var done = hasDone
            ? RequiredBooleanValue(doneValue, "done")
            : item.Done;
        if (hasDone && done != item.Done)
        {
            RequireFullWrites();
        }

        var snapshot = TodoPaperSnapshot.Capture(paper);
        if (hasText)
        {
            item.Text = text!;
        }
        if (hasDone)
        {
            item.Done = done;
            if (item.Done)
            {
                item.ReminderAt = null;
            }
        }

        if (!_controller.TryCommitMcpMutation())
        {
            snapshot.Restore(paper);
            throw SaveFailed();
        }

        _controller.RunMcpPostCommitUi(
            () => _controller.RefreshMcpTodoPaper(paper));
        return TodoDetails(item);
    }

    private object SetTodoReminder(JsonElement parameters)
    {
        RequireFullWrites();
        if (!_controller.State.ExperimentalTodoReminders)
        {
            throw new McpApiException(
                "reminders_disabled",
                "Todo reminders are disabled in PaperTodo Labs.");
        }

        var paper = RequirePaper(parameters, PaperTypes.Todo);
        var item = RequireTodo(parameters, paper);
        if (item.Done)
        {
            throw new McpApiException(
                "todo_completed",
                "A reminder cannot be set on a completed todo.");
        }
        if (!parameters.TryGetProperty(
                "reminder_at",
                out var reminderValue))
        {
            throw new McpApiException(
                "invalid_params",
                "reminder_at is required; use null to cancel.");
        }

        var reminderAt = reminderValue.ValueKind == JsonValueKind.Null
            ? (DateTimeOffset?)null
            : ParseReminderAt(RequiredStringValue(
                reminderValue,
                "reminder_at",
                80));
        var snapshot = TodoPaperSnapshot.Capture(paper);
        item.ReminderAt = reminderAt;

        if (!_controller.TryCommitMcpMutation())
        {
            snapshot.Restore(paper);
            throw SaveFailed();
        }

        _controller.RunMcpPostCommitUi(
            () => _controller.RefreshMcpTodoPaper(paper));
        return TodoDetails(item);
    }

    private object WriteNote(JsonElement parameters)
    {
        var paper = RequirePaper(parameters, PaperTypes.Note);
        var content = RequiredString(
            parameters,
            "content",
            PaperWindow.NoteTextMaxLength,
            allowEmpty: true);
        var mode = OptionalString(parameters, "mode", 20) ?? "fill_blank";
        var original = paper.Content ?? "";

        string result;
        switch (mode)
        {
            case "fill_blank":
                RequireAdditiveWrites();
                if (!string.IsNullOrEmpty(original))
                {
                    throw new McpApiException(
                        "note_not_blank",
                        "fill_blank can only write to an empty note.");
                }
                result = content;
                break;

            case "append":
                RequireAdditiveWrites();
                var separator =
                    !string.IsNullOrEmpty(original) &&
                    !string.IsNullOrEmpty(content) &&
                    !original.EndsWith('\n')
                        ? Environment.NewLine
                        : "";
                result = original + separator + content;
                break;

            case "replace":
                if (string.IsNullOrEmpty(original))
                {
                    RequireAdditiveWrites();
                }
                else if (!string.Equals(
                    original,
                    content,
                    StringComparison.Ordinal))
                {
                    RequireFullWrites();
                }
                result = content;
                break;

            default:
                throw new McpApiException(
                    "invalid_params",
                    "mode must be 'fill_blank', 'append', or 'replace'.");
        }

        if (result.Length > PaperWindow.NoteTextMaxLength)
        {
            throw new McpApiException(
                "content_too_long",
                $"A note cannot exceed {PaperWindow.NoteTextMaxLength} characters.");
        }

        paper.Content = result;
        if (!_controller.TryCommitMcpMutation())
        {
            paper.Content = original;
            throw SaveFailed();
        }

        _controller.RunMcpPostCommitUi(
            () => _controller.RefreshMcpNotePaper(paper));
        return PaperDetails(paper);
    }

    private object DeletePaper(JsonElement parameters)
    {
        RequireDeletes();
        var paper = RequirePaper(parameters);
        var target = _controller.PaperTitleText(paper);
        if (!_controller.ConfirmMcpDeletion(target))
        {
            throw new McpApiException(
                "delete_cancelled",
                "The user declined this deletion.");
        }

        var papers = _controller.State.Papers;
        var originalIndex = papers.IndexOf(paper);
        var affectedLinks = paper.Type == PaperTypes.Note
            ? _controller.State.Papers
                .Where(candidate => candidate.Type == PaperTypes.Todo)
                .SelectMany(candidate => candidate.Items)
                .Where(item => string.Equals(
                    item.LinkedNoteId,
                    paper.Id,
                    StringComparison.Ordinal))
                .Select(item => (Item: item, Link: item.LinkedNoteId))
                .ToList()
            : [];

        papers.RemoveAt(originalIndex);
        foreach (var (item, _) in affectedLinks)
        {
            item.LinkedNoteId = null;
        }

        PaperData? replacement = null;
        if (papers.Count == 0)
        {
            try
            {
                replacement = _controller.CreatePaper(
                    PaperTypes.Todo,
                    show: false);
            }
            catch
            {
                papers.Insert(originalIndex, paper);
                foreach (var (item, link) in affectedLinks)
                {
                    item.LinkedNoteId = link;
                }
                _controller.RefreshMcpAfterRollback();
                throw;
            }

            if (replacement == null)
            {
                papers.Insert(originalIndex, paper);
                foreach (var (item, link) in affectedLinks)
                {
                    item.LinkedNoteId = link;
                }
                _controller.RefreshMcpAfterRollback();
                throw new McpApiException(
                    "paper_limit",
                    "PaperTodo could not create the required replacement paper.");
            }
            replacement.IsVisible = true;
        }

        if (!_controller.TryCommitMcpMutation())
        {
            papers.Insert(originalIndex, paper);
            foreach (var (item, link) in affectedLinks)
            {
                item.LinkedNoteId = link;
            }
            if (replacement != null)
            {
                _controller.RollbackMcpCreatedPaper(replacement);
            }
            _controller.RefreshMcpAfterRollback();
            throw SaveFailed();
        }

        _controller.RunMcpPostCommitUi(
            () => _controller.FinalizeMcpPaperDeletion(
                paper,
                replacement,
                affectedLinks.Count > 0));
        return new
        {
            deleted = true,
            paper_id = paper.Id,
            replacement_paper_created = replacement != null
        };
    }

    private object DeleteTodo(JsonElement parameters)
    {
        RequireDeletes();
        var paper = RequirePaper(parameters, PaperTypes.Todo);
        var item = RequireTodo(parameters, paper);
        var target = string.IsNullOrWhiteSpace(item.Text)
            ? Strings.Get("McpUntitledTodo")
            : item.Text.Trim();
        if (!_controller.ConfirmMcpDeletion(target))
        {
            throw new McpApiException(
                "delete_cancelled",
                "The user declined this deletion.");
        }

        var snapshot = TodoPaperSnapshot.Capture(paper);
        paper.Items.Remove(item);
        if (paper.Items.Count == 0)
        {
            paper.Items.Add(new PaperItem());
        }
        NormalizeOrders(paper);

        if (!_controller.TryCommitMcpMutation())
        {
            snapshot.Restore(paper);
            throw SaveFailed();
        }

        _controller.RunMcpPostCommitUi(
            () => _controller.RefreshMcpTodoPaper(paper));
        return new
        {
            deleted = true,
            paper_id = paper.Id,
            todo_id = item.Id
        };
    }

    private List<object> AddTodoInputs(
        PaperData paper,
        IReadOnlyList<ParsedTodoInput> inputs)
    {
        var added = new List<object>(inputs.Count);
        foreach (var input in inputs)
        {
            var item = new PaperItem
            {
                Text = input.Text,
                Done = input.Done,
                Order = paper.Items.Count,
                ReminderAt = input.Done ? null : input.ReminderAt
            };
            paper.Items.Add(item);
            added.Add(TodoDetails(item));
        }
        NormalizeOrders(paper);
        return added;
    }

    private IReadOnlyList<ParsedTodoInput> ReadTodoInputs(
        JsonElement parameters,
        bool required)
    {
        if (!parameters.TryGetProperty("todos", out var todos))
        {
            if (!required)
            {
                return [];
            }
            throw new McpApiException(
                "invalid_params",
                "todos is required.");
        }
        if (todos.ValueKind == JsonValueKind.Null && !required)
        {
            return [];
        }
        if (todos.ValueKind != JsonValueKind.Array)
        {
            throw new McpApiException(
                "invalid_params",
                "todos must be an array.");
        }
        if (todos.GetArrayLength() is 0 or > PaperWindow.MaxPastedTodoLines)
        {
            throw new McpApiException(
                "invalid_params",
                $"todos must contain between 1 and {PaperWindow.MaxPastedTodoLines} items.");
        }

        var result = new List<ParsedTodoInput>(todos.GetArrayLength());
        foreach (var value in todos.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(new ParsedTodoInput(
                    RequiredStringValue(
                        value,
                        "todo",
                        PaperWindow.TodoTextMaxLength),
                    false,
                    null));
                continue;
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new McpApiException(
                    "invalid_params",
                    "Each todo must be a string or an object.");
            }

            var text = RequiredString(
                value,
                "text",
                PaperWindow.TodoTextMaxLength);
            var done = OptionalBoolean(value, "done") ?? false;
            DateTimeOffset? reminderAt = null;
            if (value.TryGetProperty(
                    "reminder_at",
                    out var reminderValue) &&
                reminderValue.ValueKind != JsonValueKind.Null)
            {
                if (!_controller.State.ExperimentalTodoReminders)
                {
                    throw new McpApiException(
                        "reminders_disabled",
                        "Todo reminders are disabled in PaperTodo Labs.");
                }
                reminderAt = ParseReminderAt(RequiredStringValue(
                    reminderValue,
                    "reminder_at",
                    80));
                if (done)
                {
                    throw new McpApiException(
                        "invalid_params",
                        "A completed todo cannot start with a reminder.");
                }
            }
            result.Add(new ParsedTodoInput(text, done, reminderAt));
        }
        return result;
    }

    private void RequireFullWritesForTodoMetadata(
        IReadOnlyList<ParsedTodoInput> inputs)
    {
        if (inputs.Any(input =>
                input.Done ||
                input.ReminderAt.HasValue))
        {
            RequireFullWrites();
        }
    }

    private object PaperDetails(PaperData paper)
    {
        if (paper.Type == PaperTypes.Note)
        {
            return new
            {
                id = paper.Id,
                type = paper.Type,
                title = _controller.PaperTitleText(paper),
                is_visible = paper.IsVisible,
                content = paper.Content ?? ""
            };
        }

        return new
        {
            id = paper.Id,
            type = paper.Type,
            title = _controller.PaperTitleText(paper),
            is_visible = paper.IsVisible,
            todos = paper.Items
                .OrderBy(item => item.Order)
                .Select(TodoDetails)
                .ToArray()
        };
    }

    private static object TodoDetails(PaperItem item) => new
    {
        id = item.Id,
        text = item.Text,
        done = item.Done,
        order = item.Order,
        reminder_at = item.ReminderAt?.ToString(
            "O",
            CultureInfo.InvariantCulture)
    };

    private PaperData RequirePaper(
        JsonElement parameters,
        string? expectedType = null)
    {
        var paperId = RequiredString(parameters, "paper_id", 64);
        var paper = _controller.State.Papers.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Id,
                paperId,
                StringComparison.Ordinal));
        if (paper == null)
        {
            throw new McpApiException(
                "paper_not_found",
                "The requested paper does not exist.");
        }
        if (expectedType != null && paper.Type != expectedType)
        {
            throw new McpApiException(
                "wrong_paper_type",
                $"This operation requires a {expectedType} paper.");
        }
        return paper;
    }

    private static PaperItem RequireTodo(
        JsonElement parameters,
        PaperData paper)
    {
        var todoId = RequiredString(parameters, "todo_id", 64);
        return paper.Items.FirstOrDefault(item =>
            string.Equals(
                item.Id,
                todoId,
                StringComparison.Ordinal))
            ?? throw new McpApiException(
                "todo_not_found",
                "The requested todo does not exist.");
    }

    private void EnsurePaperCapacity()
    {
        if (_controller.State.Papers.Count >= 100)
        {
            throw new McpApiException(
                "paper_limit",
                "PaperTodo supports at most 100 papers.");
        }
    }

    private void RequireAdditiveWrites()
    {
        if (!_controller.State.McpAllowBlankWrites &&
            !_controller.State.McpAllowFullWrites)
        {
            throw new McpApiException(
                "blank_writes_disabled",
                "Blank/additive writes are disabled in PaperTodo Settings.");
        }
    }

    private void RequireFullWrites()
    {
        if (!_controller.State.McpAllowFullWrites)
        {
            throw new McpApiException(
                "full_writes_disabled",
                "Full writes are disabled in PaperTodo Settings.");
        }
    }

    private void RequireDeletes()
    {
        if (!_controller.State.McpAllowDeletes)
        {
            throw new McpApiException(
                "deletes_disabled",
                "AI deletes are disabled in PaperTodo Settings.");
        }
    }

    private static McpApiException SaveFailed()
        => new(
            "save_failed",
            "PaperTodo could not save the change. The in-memory change was rolled back.");

    private static DateTimeOffset ParseReminderAt(string value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            throw new McpApiException(
                "invalid_params",
                "reminder_at must be ISO 8601 with a UTC offset.");
        }
        if (parsed <= DateTimeOffset.Now)
        {
            throw new McpApiException(
                "invalid_params",
                "reminder_at must be in the future.");
        }
        return parsed;
    }

    private static void NormalizeOrders(PaperData paper)
    {
        for (var index = 0; index < paper.Items.Count; index++)
        {
            paper.Items[index].Order = index;
        }
    }

    private static JsonElement RequireObject(
        JsonElement value,
        string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new McpApiException(
                "invalid_params",
                $"{name} must be an object.");
        }
        return value;
    }

    private static string RequiredString(
        JsonElement parent,
        string name,
        int maxLength,
        bool allowEmpty = false)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var value))
        {
            throw new McpApiException(
                "invalid_params",
                $"{name} is required.");
        }
        return RequiredStringValue(
            value,
            name,
            maxLength,
            allowEmpty);
    }

    private static string RequiredStringValue(
        JsonElement value,
        string name,
        int maxLength,
        bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new McpApiException(
                "invalid_params",
                $"{name} must be a string.");
        }
        var text = value.GetString() ?? "";
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw new McpApiException(
                "invalid_params",
                $"{name} cannot be empty.");
        }
        if (text.Length > maxLength)
        {
            throw new McpApiException(
                "invalid_params",
                $"{name} cannot exceed {maxLength} characters.");
        }
        return text;
    }

    private static string? OptionalString(
        JsonElement parent,
        string name,
        int maxLength,
        bool allowEmpty = false)
    {
        if (parent.ValueKind == JsonValueKind.Undefined ||
            !parent.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return RequiredStringValue(
            value,
            name,
            maxLength,
            allowEmpty);
    }

    private static bool? OptionalBoolean(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Undefined ||
            !parent.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return RequiredBooleanValue(value, name);
    }

    private static bool RequiredBooleanValue(
        JsonElement value,
        string name)
    {
        if (value.ValueKind is not JsonValueKind.True and
            not JsonValueKind.False)
        {
            throw new McpApiException(
                "invalid_params",
                $"{name} must be a boolean.");
        }
        return value.GetBoolean();
    }

    private sealed record ParsedTodoInput(
        string Text,
        bool Done,
        DateTimeOffset? ReminderAt);

    private sealed class TodoPaperSnapshot
    {
        private readonly List<PaperItemSnapshot> _items;

        private TodoPaperSnapshot(List<PaperItemSnapshot> items)
        {
            _items = items;
        }

        public static TodoPaperSnapshot Capture(PaperData paper)
            => new(paper.Items.Select(PaperItemSnapshot.Capture).ToList());

        public void Restore(PaperData paper)
        {
            paper.Items.Clear();
            foreach (var snapshot in _items)
            {
                snapshot.Restore();
                paper.Items.Add(snapshot.Item);
            }
        }
    }

    private sealed record PaperItemSnapshot(
        PaperItem Item,
        string Text,
        bool Done,
        int Order,
        string? LinkedNoteId,
        DateTimeOffset? ReminderAt)
    {
        public static PaperItemSnapshot Capture(PaperItem item)
            => new(
                item,
                item.Text,
                item.Done,
                item.Order,
                item.LinkedNoteId,
                item.ReminderAt);

        public void Restore()
        {
            Item.Text = Text;
            Item.Done = Done;
            Item.Order = Order;
            Item.LinkedNoteId = LinkedNoteId;
            Item.ReminderAt = ReminderAt;
        }
    }
}
