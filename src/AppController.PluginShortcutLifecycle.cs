namespace PaperTodo;

public sealed partial class AppController
{
    private void SuspendPluginShortcutRegistrations()
    {
        if (_pluginHotkeys == null)
        {
            return;
        }

        _pluginHotkeys.Invoked -= OnPluginHotkeyInvoked;
        _pluginHotkeys.Dispose();
        _pluginHotkeys = null;
    }
}
