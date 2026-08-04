using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private int _pluginStartupPaperGeneration;

    private void SchedulePluginStartupPapers(StartupCommandKind visibilityCommand)
    {
        var generation = ++_pluginStartupPaperGeneration;
        if (visibilityCommand == StartupCommandKind.Hide || IsExiting)
        {
            return;
        }

        var candidates = PaperBodyPlugins.Descriptors
            .Where(descriptor => descriptor.Manifest?.StartupPaper != null)
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        // Normal paper restoration has completed before this method is called. One idle
        // dispatch is enough; startup timing is owned by the host, not by each plugin.
        _ = dispatcher.BeginInvoke(
            (Action)(() =>
            {
                if (generation != _pluginStartupPaperGeneration ||
                    IsExiting)
                {
                    return;
                }
                EnsurePluginStartupPapers(candidates);
            }),
            DispatcherPriority.ApplicationIdle);
    }

    private void EnsurePluginStartupPapers(
        IReadOnlyList<PaperBodyPluginDescriptor> descriptors)
    {
        var changed = false;
        foreach (var descriptor in descriptors)
        {
            var startup = descriptor.Manifest?.StartupPaper;
            if (startup == null || !StartupSettingEnabled(descriptor, startup))
            {
                continue;
            }

            var paper = State.Papers.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.StartupOwnerPluginId,
                    descriptor.Id,
                    StringComparison.Ordinal) &&
                string.Equals(
                    candidate.StartupInstanceKey,
                    startup.InstanceKey,
                    StringComparison.Ordinal));
            if (paper != null &&
                (!string.Equals(
                     paper.BodyProviderId,
                     descriptor.Id,
                     StringComparison.Ordinal) ||
                 paper.Type != PaperTypes.Note))
            {
                // The user repurposed the previously generated paper. Do not take it over or
                // create a duplicate behind their back.
                continue;
            }

            if (paper == null)
            {
                paper = CreatePaper(PaperTypes.Note, show: false);
                if (paper == null)
                {
                    continue;
                }
                paper.BodyProviderId = descriptor.Id;
                paper.StartupOwnerPluginId = descriptor.Id;
                paper.StartupInstanceKey = startup.InstanceKey;
                if (!string.IsNullOrWhiteSpace(startup.Title))
                {
                    paper.Title = PaperTitles.CleanCustomTitle(
                        startup.Title,
                        State.MaxTitleLength);
                }
                changed = true;
            }

            var collapsed = startup.Presentation == "capsule";
            if (!paper.IsVisible || paper.IsCollapsed != collapsed)
            {
                paper.IsVisible = true;
                paper.IsCollapsed = collapsed;
                changed = true;
            }
            ShowPaper(paper, activate: false);
        }

        if (!changed)
        {
            return;
        }
        ArrangeDeepCapsules(
            animate: State.EnableAnimations,
            flushInitialPresentations: true);
        RefreshTrayMenu();
        MarkDirty();
    }

    private bool StartupSettingEnabled(
        PaperBodyPluginDescriptor descriptor,
        PaperBodyPluginStartupManifest startup)
    {
        try
        {
            using var document = JsonDocument.Parse(
                PaperBodyPlugins.DataStore.GetSettingsJson(descriptor));
            return document.RootElement.TryGetProperty(
                    startup.EnabledSetting,
                    out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                value.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
