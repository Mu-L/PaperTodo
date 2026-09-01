namespace PaperTodo;

/// <summary>
/// Internal renderer boundary for the built-in Markdown editor. The editor owns text, caret,
/// selection, undo and image interaction; a renderer session owns only AvalonEdit presentation.
/// Keep this internal until the contract has survived the Markdig migration and UI regression pass.
/// </summary>
internal interface IMarkdownRendererProvider
{
    string Id { get; }

    IMarkdownRendererSession Attach(
        MarkdownTextBox editor,
        MarkdownSemanticDocument semanticDocument);
}

internal interface IMarkdownRendererSession : IDisposable
{
}

internal sealed class BuiltinMarkdownRendererProvider : IMarkdownRendererProvider
{
    public static BuiltinMarkdownRendererProvider Instance { get; } = new();

    private BuiltinMarkdownRendererProvider()
    {
    }

    public string Id => "builtin.markdig";

    public IMarkdownRendererSession Attach(
        MarkdownTextBox editor,
        MarkdownSemanticDocument semanticDocument)
    {
        return new MarkdownSemanticPresentation(editor, semanticDocument);
    }
}
