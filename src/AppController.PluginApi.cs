using System.Windows;
using PaperTodo.Plugin;

namespace PaperTodo;

public sealed partial class AppController
{
    private PaperBodyPluginEventHub? _paperBodyPluginEvents;
    private PaperCommandService? _paperCommands;
    private readonly HashSet<string> _pendingPluginPaperStateDeletes =
        new(StringComparer.Ordinal);

    internal PaperBodyPluginEventHub PaperBodyPluginEvents =>
        _paperBodyPluginEvents ??= new PaperBodyPluginEventHub(
            this,
            Application.Current.Dispatcher);

    internal PaperCommandService PaperCommands =>
        _paperCommands ??= new PaperCommandService(this);

    internal PaperSnapshot CapturePaperSnapshot(PaperData paper) =>
        new(
            paper.Id,
            paper.Type,
            PaperTitleText(paper),
            paper.IsVisible,
            paper.IsCollapsed,
            paper.AlwaysOnTop,
            paper.BodyProviderId);

    internal TodoSnapshot CaptureTodoSnapshot(PaperData paper, PaperItem item) =>
        new(
            paper.Id,
            PaperTitleText(paper),
            item.Id,
            item.Text,
            item.Done,
            item.Order,
            item.LinkedPaperId,
            item.LinkedPath,
            item.ReminderAt);

    internal NoteSnapshot CaptureNoteSnapshot(PaperData paper)
    {
        var contentAvailable = paper.Type == PaperTypes.Note &&
            string.Equals(
                paper.BodyProviderId,
                PaperBodyProviderIds.Markdown,
                StringComparison.Ordinal);
        return new NoteSnapshot(
            paper.Id,
            PaperTitleText(paper),
            paper.BodyProviderId,
            contentAvailable,
            contentAvailable ? paper.Content ?? "" : "");
    }

    internal void PrepareExternalPaperOperation()
    {
        CommitPendingNoteContentsForSave();
        _paperBodyPluginEvents?.FlushUserChanges();
    }

    internal IDisposable SuppressPaperPluginEventScans() =>
        _paperBodyPluginEvents?.SuppressScans() ?? EmptyDisposable.Instance;

    internal void PublishExternalPaperOperation(PaperOperationContext context) =>
        _paperBodyPluginEvents?.ScanNow(context);

    internal void ResetPaperPluginEventBaseline() =>
        _paperBodyPluginEvents?.ResetBaseline();

    internal bool TryCommitExternalMutation()
    {
        MarkDirty();
        return TrySaveNow(sync: true);
    }

    internal void RunExternalPostCommitUi(Action update) =>
        RunMcpPostCommitUi(update);

    internal void RollbackExternalCreatedPaper(PaperData paper) =>
        RollbackMcpCreatedPaper(paper);

    internal void FinalizeExternalPaperCreated(PaperData paper, bool show) =>
        FinalizeMcpPaperCreated(paper, show);

    internal void RefreshExternalTodoPaper(PaperData paper) =>
        RefreshMcpTodoPaper(paper);

    internal void RefreshExternalNotePaper(PaperData paper) =>
        RefreshMcpNotePaper(paper);

    internal void FinalizeExternalPaperDeletion(
        PaperData deleted,
        PaperData? replacement,
        bool refreshLinkedTodos) =>
        FinalizeMcpPaperDeletion(deleted, replacement, refreshLinkedTodos);

    internal void RefreshAfterExternalRollback() =>
        RefreshMcpAfterRollback();

    internal void UpdatePaperTitleFromPlugin(
        PaperData paper,
        string title,
        string providerId)
    {
        PrepareExternalPaperOperation();
        using (SuppressPaperPluginEventScans())
        {
            UpdatePaperTitle(paper, title);
        }
        PublishExternalPaperOperation(PaperOperationContext.Plugin(providerId));
    }

    internal void QueuePluginPaperStateDeletion(string paperId)
    {
        if (!string.IsNullOrWhiteSpace(paperId))
        {
            _pendingPluginPaperStateDeletes.Add(paperId);
        }
    }

    internal void TryFlushPendingPluginPaperStateDeletes()
    {
        foreach (var paperId in _pendingPluginPaperStateDeletes.ToArray())
        {
            if (State.Papers.Any(paper =>
                    string.Equals(paper.Id, paperId, StringComparison.Ordinal)))
            {
                _pendingPluginPaperStateDeletes.Remove(paperId);
                continue;
            }

            try
            {
                _paperBodyPlugins.DataStore.RemovePaperStateEverywhere(paperId);
                _pendingPluginPaperStateDeletes.Remove(paperId);
            }
            catch
            {
                // Main data is already authoritative. Retry this independent cleanup after a
                // later successful save without converting it into a core save failure.
            }
        }
    }

    internal void DisposePaperPluginHostRuntime()
    {
        _paperBodyPluginEvents?.Dispose();
        _paperBodyPluginEvents = null;
        _paperCommands = null;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
