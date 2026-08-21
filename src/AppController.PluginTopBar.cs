using System.Diagnostics;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed record PluginTopBarActionBinding(
    Guid SessionId,
    string ProviderId,
    PaperTopBarActionScope Scope,
    PaperTopBarAction Action);

internal sealed record PluginTopBarRenderState(
    IReadOnlyList<PluginTopBarActionBinding> Actions,
    PaperHostTopBarActions HiddenHostActions);

public sealed partial class AppController
{
    private const int MaximumPaperTopBarActions = 4;
    private const int MaximumGlobalTopBarActions = 2;
    private const int MaximumTopBarActionIdLength = 64;
    private const int MaximumTopBarToolTipLength = 160;
    private const int MaximumTopBarCharacterLength = 8;
    private const int MaximumTopBarSvgPathLength = 4096;

    private sealed class PluginTopBarSessionRegistration
    {
        public required Guid SessionId { get; init; }
        public required string ProviderId { get; init; }
        public required string HostPaperId { get; init; }
        public required Func<bool> IsActive { get; set; }
        public required Action<PaperTopBarActionInvocation> Invoke { get; set; }
        public long Ordinal { get; set; }
        public PaperTopBarAction[] PaperActions { get; set; } = [];
        public PaperHostTopBarActions HiddenHostActions { get; set; }
        public PaperTopBarAction[] GlobalActions { get; set; } = [];
    }

    private readonly Dictionary<Guid, PluginTopBarSessionRegistration>
        _pluginTopBarSessions = new();
    private long _pluginTopBarRegistrationOrdinal;

    internal void SetPluginPaperTopBarActions(
        Guid sessionId,
        string providerId,
        string hostPaperId,
        IReadOnlyList<PaperTopBarAction> actions,
        PaperHostTopBarActions hiddenHostActions,
        Func<bool> isActive,
        Action<PaperTopBarActionInvocation> invoke)
    {
        EnsurePluginTopBarProtocol(providerId);
        var normalized = NormalizePluginTopBarActions(
            actions,
            MaximumPaperTopBarActions,
            "paper");
        const PaperHostTopBarActions supportedHidden =
            PaperHostTopBarActions.NewTodoPaper |
            PaperHostTopBarActions.NewNotePaper;
        if ((hiddenHostActions & ~supportedHidden) != 0)
        {
            throw new PaperTodoPluginException(
                "invalid_topbar_host_action",
                "Only the host's new-Todo and new-Note actions can be hidden by a plugin paper.");
        }

        var registration = GetOrCreatePluginTopBarRegistration(
            sessionId,
            providerId,
            hostPaperId,
            isActive,
            invoke);
        registration.PaperActions = normalized;
        registration.HiddenHostActions = hiddenHostActions;
        registration.Ordinal = ++_pluginTopBarRegistrationOrdinal;

        if (normalized.Length > 0 || hiddenHostActions != PaperHostTopBarActions.None)
        {
            PaperWindow.EnsurePluginTopBarLoadedHandler();
        }
        RefreshPluginTopBarForPaper(hostPaperId);
    }

    internal void SetPluginGlobalTopBarActions(
        Guid sessionId,
        string providerId,
        string hostPaperId,
        IReadOnlyList<PaperTopBarAction> actions,
        Func<bool> isActive,
        Action<PaperTopBarActionInvocation> invoke)
    {
        EnsurePluginTopBarProtocol(providerId);
        var normalized = NormalizePluginTopBarActions(
            actions,
            MaximumGlobalTopBarActions,
            "global");
        var registration = GetOrCreatePluginTopBarRegistration(
            sessionId,
            providerId,
            hostPaperId,
            isActive,
            invoke);
        registration.GlobalActions = normalized;
        registration.Ordinal = ++_pluginTopBarRegistrationOrdinal;

        if (normalized.Length > 0)
        {
            PaperWindow.EnsurePluginTopBarLoadedHandler();
        }
        RefreshAllPluginTopBars();
    }

    private void EnsurePluginTopBarProtocol(string providerId)
    {
        if (PaperBodyPlugins.TryGet(providerId, out var descriptor) &&
            string.Equals(
                descriptor.ApiVersion,
                PaperBodyPluginRegistry.SupportedPluginApiVersion,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new PaperTodoPluginException(
            "topbar_requires_api_2_0",
            "Plugin top-bar extensions require apiVersion 2.0.");
    }

    internal void RemovePluginTopBarSession(Guid sessionId)
    {
        if (!_pluginTopBarSessions.Remove(sessionId, out var removed))
        {
            return;
        }

        if (removed.GlobalActions.Length > 0)
        {
            RefreshAllPluginTopBars();
        }
        else
        {
            RefreshPluginTopBarForPaper(removed.HostPaperId);
        }
    }

    internal PluginTopBarRenderState GetPluginTopBarRenderState(string paperId)
    {
        PruneInactivePluginTopBarSessions();

        var paperRegistration = _pluginTopBarSessions.Values
            .Where(item =>
                string.Equals(item.HostPaperId, paperId, StringComparison.Ordinal) &&
                IsPluginTopBarRegistrationActive(item))
            .OrderByDescending(item => item.Ordinal)
            .FirstOrDefault();

        var actions = new List<PluginTopBarActionBinding>();
        foreach (var registration in _pluginTopBarSessions.Values
                     .Where(item =>
                         item.GlobalActions.Length > 0 &&
                         IsPluginTopBarRegistrationActive(item))
                     .GroupBy(item => item.ProviderId, StringComparer.Ordinal)
                     .Select(group => group
                         .OrderByDescending(item => item.Ordinal)
                         .First())
                     .OrderBy(item => item.Ordinal))
        {
            foreach (var action in registration.GlobalActions)
            {
                actions.Add(new PluginTopBarActionBinding(
                    registration.SessionId,
                    registration.ProviderId,
                    PaperTopBarActionScope.Global,
                    action));
            }
        }

        if (paperRegistration != null)
        {
            foreach (var action in paperRegistration.PaperActions)
            {
                actions.Add(new PluginTopBarActionBinding(
                    paperRegistration.SessionId,
                    paperRegistration.ProviderId,
                    PaperTopBarActionScope.Paper,
                    action));
            }
        }

        return new PluginTopBarRenderState(
            actions,
            paperRegistration?.HiddenHostActions ?? PaperHostTopBarActions.None);
    }

    internal void InvokePluginTopBarAction(
        PluginTopBarActionBinding binding,
        string targetPaperId,
        string targetPaperType,
        string targetBodyProviderId)
    {
        if (!_pluginTopBarSessions.TryGetValue(binding.SessionId, out var registration) ||
            !IsPluginTopBarRegistrationActive(registration))
        {
            RemovePluginTopBarSession(binding.SessionId);
            return;
        }

        var actions = binding.Scope == PaperTopBarActionScope.Global
            ? registration.GlobalActions
            : registration.PaperActions;
        if (!actions.Any(action =>
                string.Equals(action.Id, binding.Action.Id, StringComparison.Ordinal) &&
                action.Visible &&
                action.Enabled))
        {
            return;
        }

        try
        {
            registration.Invoke(new PaperTopBarActionInvocation(
                binding.Action.Id,
                binding.Scope,
                targetPaperId,
                targetPaperType,
                targetBodyProviderId));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "Plugin top-bar action failed. Provider={0}; Action={1}; Exception={2}",
                binding.ProviderId,
                binding.Action.Id,
                ex.GetBaseException());
        }
    }

    private PluginTopBarSessionRegistration GetOrCreatePluginTopBarRegistration(
        Guid sessionId,
        string providerId,
        string hostPaperId,
        Func<bool> isActive,
        Action<PaperTopBarActionInvocation> invoke)
    {
        if (_pluginTopBarSessions.TryGetValue(sessionId, out var existing))
        {
            existing.IsActive = isActive;
            existing.Invoke = invoke;
            return existing;
        }

        var created = new PluginTopBarSessionRegistration
        {
            SessionId = sessionId,
            ProviderId = providerId,
            HostPaperId = hostPaperId,
            IsActive = isActive,
            Invoke = invoke,
            Ordinal = ++_pluginTopBarRegistrationOrdinal
        };
        _pluginTopBarSessions.Add(sessionId, created);
        return created;
    }

    private static PaperTopBarAction[] NormalizePluginTopBarActions(
        IReadOnlyList<PaperTopBarAction>? actions,
        int maximumCount,
        string scope)
    {
        actions ??= [];
        if (actions.Count > maximumCount)
        {
            throw new PaperTodoPluginException(
                "too_many_topbar_actions",
                $"A plugin can contribute at most {maximumCount} {scope} top-bar actions.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PaperTopBarAction>(actions.Count);
        foreach (var source in actions)
        {
            if (source == null)
            {
                throw new PaperTodoPluginException(
                    "invalid_topbar_action",
                    "Top-bar actions cannot contain null entries.");
            }

            var id = source.Id?.Trim() ?? "";
            if (id.Length is 0 or > MaximumTopBarActionIdLength ||
                id.Any(ch =>
                    !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')) ||
                !seen.Add(id))
            {
                throw new PaperTodoPluginException(
                    "invalid_topbar_action_id",
                    "Top-bar action ids must be unique 1-64 character ASCII identifiers using letters, digits, '.', '_' or '-'.");
            }

            var tooltip = source.ToolTip?.Trim() ?? "";
            if (tooltip.Length > MaximumTopBarToolTipLength)
            {
                throw new PaperTodoPluginException(
                    "invalid_topbar_tooltip",
                    $"Top-bar tooltips cannot exceed {MaximumTopBarToolTipLength} characters.");
            }

            var icon = source.Icon ?? new PaperTopBarIcon();
            var value = icon.Value?.Trim() ?? "";
            switch (icon.Kind)
            {
                case PaperTopBarIconKind.Character:
                    if (value.Length is 0 or > MaximumTopBarCharacterLength)
                    {
                        throw new PaperTodoPluginException(
                            "invalid_topbar_icon",
                            $"Character top-bar icons must contain 1-{MaximumTopBarCharacterLength} UTF-16 characters.");
                    }
                    break;
                case PaperTopBarIconKind.SvgPath:
                    if (value.Length is 0 or > MaximumTopBarSvgPathLength)
                    {
                        throw new PaperTodoPluginException(
                            "invalid_topbar_icon",
                            $"SVG path data must contain 1-{MaximumTopBarSvgPathLength} characters.");
                    }
                    try
                    {
                        _ = Geometry.Parse(value);
                    }
                    catch (Exception ex)
                    {
                        throw new PaperTodoPluginException(
                            "invalid_topbar_icon",
                            $"SVG path data is invalid: {ex.GetBaseException().Message}");
                    }
                    break;
                default:
                    throw new PaperTodoPluginException(
                        "invalid_topbar_icon",
                        "Unknown top-bar icon kind.");
            }

            result.Add(source with
            {
                Id = id,
                ToolTip = tooltip,
                Icon = icon with { Value = value }
            });
        }
        return result.ToArray();
    }

    private void PruneInactivePluginTopBarSessions()
    {
        foreach (var registration in _pluginTopBarSessions.Values.ToArray())
        {
            if (!IsPluginTopBarRegistrationActive(registration))
            {
                _pluginTopBarSessions.Remove(registration.SessionId);
            }
        }
    }

    private static bool IsPluginTopBarRegistrationActive(
        PluginTopBarSessionRegistration registration)
    {
        try
        {
            return registration.IsActive();
        }
        catch
        {
            return false;
        }
    }

    private void RefreshPluginTopBarForPaper(string paperId)
    {
        if (_windows.TryGetValue(paperId, out var window) && !window.IsClosed)
        {
            window.RefreshPluginTopBarActions();
        }
    }

    private void RefreshAllPluginTopBars()
    {
        foreach (var window in _windows.Values.ToArray())
        {
            if (!window.IsClosed)
            {
                window.RefreshPluginTopBarActions();
            }
        }
    }
}
