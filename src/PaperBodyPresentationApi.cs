using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed class PaperBodyPresentationApi : IPaperPresentationApi
{
    private readonly Action<bool> _show;
    private readonly Action _hide;
    private readonly Action<bool> _toggleVisibility;
    private readonly Action<bool> _expand;
    private readonly Action _collapse;
    private readonly Action<bool> _toggleCollapsed;
    private readonly Action _activate;

    public PaperBodyPresentationApi(
        string paperId,
        Action<bool> show,
        Action hide,
        Action<bool> toggleVisibility,
        Action<bool> expand,
        Action collapse,
        Action<bool> toggleCollapsed,
        Action activate)
    {
        PaperId = paperId;
        _show = show;
        _hide = hide;
        _toggleVisibility = toggleVisibility;
        _expand = expand;
        _collapse = collapse;
        _toggleCollapsed = toggleCollapsed;
        _activate = activate;
    }

    public string PaperId { get; }

    public void Show(bool activate = true) => _show(activate);
    public void Hide() => _hide();
    public void ToggleVisibility(bool activate = true) => _toggleVisibility(activate);
    public void Expand(bool activate = true) => _expand(activate);
    public void Collapse() => _collapse();
    public void ToggleCollapsed(bool activate = true) => _toggleCollapsed(activate);
    public void Activate() => _activate();
}
