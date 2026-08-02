using System.Text.Json;
using System.Windows.Threading;

namespace PaperTodo;

public sealed partial class AppController
{
    private DispatcherTimer? _pluginStartupPaperTimer;
    private int _pluginStartupPaperGeneration;

    private void SchedulePluginStartupPapers(StartupCommandKind visibilityCommand)
    {
        _pluginStartupPaperTimer?.Stop();
        _pluginStartupPaperTimer = null;
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

        var delay = candidates
            .Select(item => item.Manifest!.StartupPaper!.DelayMs)
            .DefaultIfEmpty(1200)
            .Min();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(delay)
        };
        _pluginStartupPaperTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_pluginStartupPaperTimer, timer))
            {
                _pluginStartupPaperTimer = null;
            }
            if (generation != _pluginStartupPaperGeneration || IsExiting)
            {
                return;
            }
            EnsurePluginStartupPapers(candidates);
        };
        timer.Start();
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
