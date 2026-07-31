using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private McpApiHost? _mcpApiHost;
    private McpCommandService? _mcpCommands;

    private void RefreshMcpRuntime()
    {
        if (IsExiting || !State.McpEnabled)
        {
            DisposeMcpRuntime();
            return;
        }

        _mcpCommands ??= new McpCommandService(this);
        _mcpApiHost ??= new McpApiHost(
            Application.Current.Dispatcher,
            _mcpCommands);
        _mcpApiHost.Start();
    }

    private void DisposeMcpRuntime()
    {
        _mcpApiHost?.Dispose();
        _mcpApiHost = null;
        _mcpCommands = null;
    }

    private void ToggleMcpEnabled()
    {
        State.McpEnabled = !State.McpEnabled;
        RefreshMcpRuntime();
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleMcpBlankWrites()
    {
        State.McpAllowBlankWrites = !State.McpAllowBlankWrites;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleMcpFullWrites()
    {
        State.McpAllowFullWrites = !State.McpAllowFullWrites;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    private void ToggleMcpDeletes()
    {
        State.McpAllowDeletes = !State.McpAllowDeletes;
        SaveNow();
        RefreshSettingsWindowContent();
    }

    internal bool TryCommitMcpMutation()
    {
        MarkDirty();
        return TrySaveNow(sync: true);
    }

    internal void RunMcpPostCommitUi(Action update)
    {
        try
        {
            update();
        }
        catch
        {
            // Persistence is already committed. Retry UI reconciliation once without
            // turning a successful MCP mutation into an error that may be replayed.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || IsExiting)
            {
                return;
            }

            try
            {
                _ = dispatcher.BeginInvoke(
                    (Action)(() =>
                    {
                        if (IsExiting)
                        {
                            return;
                        }

                        try
                        {
                            update();
                        }
                        catch
                        {
                            try
                            {
                                ArrangeDeepCapsules(animate: false);
                                RefreshTrayMenu();
                            }
                            catch
                            {
                                // The persisted state remains authoritative.
                            }
                        }
                    }),
                    DispatcherPriority.ContextIdle);
            }
            catch
            {
                // Dispatcher shutdown cannot invalidate the committed data.
            }
        }
    }

    internal void RollbackMcpCreatedPaper(PaperData paper)
    {
        State.Papers.Remove(paper);
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            TryExitCleanup(() =>
                window.CloseForReal(saveBeforeClose: false));
            _windows.Remove(paper.Id);
        }

        _visibilityAnimationVersions.Remove(paper.Id);
        TryExitCleanup(NotifyTodoReminderCollectionChanged);
        TryExitCleanup(() => ArrangeDeepCapsules(animate: false));
        TryExitCleanup(RefreshTrayMenu);
    }

    internal void FinalizeMcpPaperCreated(
        PaperData paper,
        bool show)
    {
        paper.IsVisible = show;
        RefreshTrayMenu();
        if (show)
        {
            ShowPaper(paper);
        }
        else
        {
            ArrangeDeepCapsules(animate: false);
        }
    }

    internal void RefreshMcpTodoPaper(PaperData paper)
    {
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.RefreshTodoRowsForExternalChange();
        }
        NotifyTodoReminderCollectionChanged();
        RefreshTrayMenu();
    }

    internal void RefreshMcpNotePaper(PaperData paper)
    {
        if (_windows.TryGetValue(paper.Id, out var window))
        {
            window.RefreshNoteForExternalChange();
        }
        RefreshTodoRowsForLinkedNote(paper.Id);
        RefreshTrayMenu();
    }

    internal bool ConfirmMcpDeletion(string target)
    {
        var owner = _settingsWindow is { IsVisible: true }
            ? _settingsWindow
            : _windows.Values.FirstOrDefault(window => window.IsVisible);
        var message = Strings.Format("McpDeleteConfirmBody", target);
        var result = owner != null
            ? MessageBox.Show(
                owner,
                message,
                Strings.Get("McpDeleteConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No)
            : MessageBox.Show(
                message,
                Strings.Get("McpDeleteConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    internal void FinalizeMcpPaperDeletion(
        PaperData deleted,
        PaperData? replacement,
        bool refreshLinkedTodos)
    {
        deleted.IsVisible = false;
        NextVisibilityAnimationVersion(deleted.Id);
        if (_windows.TryGetValue(deleted.Id, out var window))
        {
            RestoreExperimentalPassiveForWindow(window);
            window.CloseForReal(saveBeforeClose: false);
            _windows.Remove(deleted.Id);
        }
        _visibilityAnimationVersions.Remove(deleted.Id);
        NotifyTodoReminderCollectionChanged();

        if (refreshLinkedTodos)
        {
            foreach (var todo in State.Papers.Where(
                         paper => paper.Type == PaperTypes.Todo))
            {
                if (_windows.TryGetValue(todo.Id, out var todoWindow))
                {
                    todoWindow.RefreshTodoRowsForExternalChange();
                }
            }
            RefreshCapsuleEligibilityForLinkedNotes();
        }

        if (replacement != null)
        {
            replacement.IsVisible = true;
            ShowPaper(replacement);
        }

        ArrangeDeepCapsules(animate: false);
        RefreshTrayMenu();
    }

    internal void RefreshMcpAfterRollback()
    {
        TryExitCleanup(() => ArrangeDeepCapsules(animate: false));
        TryExitCleanup(RefreshTrayMenu);
    }

    private static string BuildCodexMcpConfiguration()
    {
        var executable = Environment.ProcessPath ??
            Path.Combine(AppContext.BaseDirectory, "PaperTodo.exe");
        return string.Join(
            Environment.NewLine,
            "[mcp_servers.papertodo]",
            $"command = {JsonSerializer.Serialize(executable)}",
            $"args = [{JsonSerializer.Serialize(McpBridge.CommandLineSwitch)}]");
    }

    private static string BuildJsonMcpConfiguration()
    {
        var executable = Environment.ProcessPath ??
            Path.Combine(AppContext.BaseDirectory, "PaperTodo.exe");
        return JsonSerializer.Serialize(
            new
            {
                mcpServers = new
                {
                    papertodo = new
                    {
                        command = executable,
                        args = new[] { McpBridge.CommandLineSwitch }
                    }
                }
            },
            new JsonSerializerOptions { WriteIndented = true });
    }
}
