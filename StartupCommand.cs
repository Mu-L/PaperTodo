namespace PaperTodo;

public enum StartupCommandKind
{
    None,
    Show,
    Hide,
    Toggle,
    NewTodo,
    NewNote,
    Exit
}

public sealed class StartupCommand
{
    public StartupCommand(StartupCommandKind kind)
    {
        Kind = kind;
    }

    public StartupCommandKind Kind { get; }

    public bool CreatesPaper => Kind is StartupCommandKind.NewTodo or StartupCommandKind.NewNote;

    public static StartupCommand Parse(
        IReadOnlyList<string> args,
        StartupCommandKind defaultWhenEmpty = StartupCommandKind.None)
    {
        var command = args
            .Select(Normalize)
            .FirstOrDefault(arg => !string.IsNullOrWhiteSpace(arg));

        if (string.IsNullOrWhiteSpace(command))
        {
            return new StartupCommand(defaultWhenEmpty);
        }

        return command switch
        {
            "show" or "open" => new StartupCommand(StartupCommandKind.Show),
            "hide" => new StartupCommand(StartupCommandKind.Hide),
            "toggle" => new StartupCommand(StartupCommandKind.Toggle),
            "new-todo" or "todo" => new StartupCommand(StartupCommandKind.NewTodo),
            "new-note" or "note" or "paper" => new StartupCommand(StartupCommandKind.NewNote),
            "exit" or "quit" => new StartupCommand(StartupCommandKind.Exit),
            _ => new StartupCommand(StartupCommandKind.None)
        };
    }

    private static string Normalize(string arg)
    {
        return arg.Trim().TrimStart('-', '/').ToLowerInvariant();
    }
}
