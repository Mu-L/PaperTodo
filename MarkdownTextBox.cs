using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace PaperTodo;

public sealed class MarkdownTextBox : TextBox
{
    public void WrapSelection(string prefix, string suffix)
    {
        var start = SelectionStart;
        var length = SelectionLength;
        var selected = SelectedText ?? "";

        SelectedText = prefix + selected + suffix;
        Focus();

        if (length == 0)
        {
            SelectionStart = start + prefix.Length;
            SelectionLength = 0;
        }
        else
        {
            SelectionStart = start + prefix.Length;
            SelectionLength = length;
        }
    }

    public void InsertMarkdownLink()
    {
        var start = SelectionStart;
        var selected = string.IsNullOrWhiteSpace(SelectedText) ? Strings.Get("MarkdownDefaultLinkLabel") : SelectedText;
        var markdown = $"[{selected}](https://)";

        SelectedText = markdown;
        Focus();

        var urlStart = start + markdown.LastIndexOf("https://", StringComparison.Ordinal);
        SelectionStart = urlStart;
        SelectionLength = "https://".Length;
    }

    public void InsertLinePrefix(string prefix)
    {
        var start = SelectionStart;
        var lineStart = Text.LastIndexOf('\n', Math.Max(0, start - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        SelectionStart = lineStart;
        SelectionLength = 0;
        SelectedText = prefix;

        Focus();
        SelectionStart = start + prefix.Length;
        SelectionLength = 0;
    }
}
