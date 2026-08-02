using System.IO;

namespace PaperTodo;

internal sealed class PaperBodyPluginStartupManifest
{
    public string EnabledSetting { get; set; } = "";
    public string InstanceKey { get; set; } = "main";
    public string Presentation { get; set; } = "capsule";
    public string Title { get; set; } = "";
    public int DelayMs { get; set; } = 1200;
}

internal sealed partial class PaperBodyPluginRegistry
{
    private static void ValidateStartupPaper(PaperBodyPluginManifest manifest)
    {
        var startup = manifest.StartupPaper;
        if (startup == null)
        {
            return;
        }

        startup.EnabledSetting = startup.EnabledSetting?.Trim() ?? "";
        startup.InstanceKey = startup.InstanceKey?.Trim() ?? "";
        startup.Presentation = startup.Presentation?.Trim().ToLowerInvariant() ?? "";
        startup.Title = startup.Title?.Trim() ?? "";
        startup.DelayMs = Math.Clamp(startup.DelayMs, 500, 5000);

        if (startup.InstanceKey.Length is < 1 or > 80 ||
            !startup.InstanceKey.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-'))
        {
            throw new InvalidDataException(
                "startupPaper.instanceKey must contain 1-80 ASCII letters, digits, '.', '_' or '-'.");
        }
        if (startup.Presentation is not ("capsule" or "expanded"))
        {
            throw new InvalidDataException(
                "startupPaper.presentation must be 'capsule' or 'expanded'.");
        }
        if (startup.Title.Length > 120)
        {
            throw new InvalidDataException(
                "startupPaper.title cannot exceed 120 characters.");
        }
        if (startup.EnabledSetting.Length == 0)
        {
            throw new InvalidDataException(
                "startupPaper.enabledSetting must name a boolean plugin setting.");
        }

        var setting = manifest.Settings.FirstOrDefault(item =>
            string.Equals(
                item.Id,
                startup.EnabledSetting,
                StringComparison.Ordinal));
        if (setting == null || setting.Type != "boolean")
        {
            throw new InvalidDataException(
                $"startupPaper.enabledSetting '{startup.EnabledSetting}' must reference a boolean setting.");
        }
    }
}
