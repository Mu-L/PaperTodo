namespace PaperTodo.Plugin;

/// <summary>
/// Presentation controls for the paper that owns the current plugin body session. PaperTodo keeps
/// ownership of window lifetime, geometry, animation, capsule layout, focus and activation rules;
/// plugins can only request state changes for their own host paper.
/// </summary>
public interface IPaperPresentationApi
{
    string PaperId { get; }

    void Show(bool activate = true);
    void Hide();
    void ToggleVisibility(bool activate = true);

    void Expand(bool activate = true);
    void Collapse();
    void ToggleCollapsed(bool activate = true);

    void Activate();
}
