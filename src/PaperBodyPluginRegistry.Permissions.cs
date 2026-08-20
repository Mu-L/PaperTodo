using System.Collections.Frozen;
using System.IO;
using System.Text.Json;
using PaperTodo.Plugin;

namespace PaperTodo;

internal sealed partial class PaperBodyPluginRegistry
{
    private static IReadOnlySet<string> ParsePermissions(IEnumerable<string>? values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values ?? [])
        {
            var value = raw?.Trim() ?? "";
            if (value.Length == 0)
            {
                continue;
            }
            if (!PaperTodoPermissionNames.All.Contains(value))
            {
                throw new InvalidDataException(
                    $"Unknown plugin permission '{value}'.");
            }
            result.Add(value);
        }
        return result.ToFrozenSet(StringComparer.Ordinal);
    }

    private static void ValidateProtocolFeatures(PaperBodyPluginManifest manifest)
    {
        manifest.Permissions ??= [];
        if (manifest.Permissions.Length > 0 && !ApiAtLeast(manifest.ApiVersion, 1, 3))
        {
            throw new InvalidDataException(
                "Plugin permissions require apiVersion 1.3 or newer.");
        }
    }

    private static bool ApiAtLeast(string apiVersion, int major, int minor)
    {
        var parts = apiVersion.Split('.');
        return int.Parse(parts[0]) > major ||
            (int.Parse(parts[0]) == major && int.Parse(parts[1]) >= minor);
    }

    private static void ValidateDeclaredDefault(
        PaperBodyPluginSettingManifest setting)
    {
        var value = setting.Default;
        switch (setting.Type)
        {
            case "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return;

            case "string" when value.ValueKind == JsonValueKind.String:
                var text = value.GetString() ?? "";
                if (setting.MaxLength is > 0 && text.Length > setting.MaxLength.Value)
                {
                    throw new InvalidDataException(
                        $"Plugin setting '{setting.Id}' default exceeds maxLength.");
                }
                return;

            case "number" when value.ValueKind == JsonValueKind.Number &&
                               value.TryGetDouble(out var number) &&
                               double.IsFinite(number):
                if (setting.Min.HasValue && number < setting.Min.Value ||
                    setting.Max.HasValue && number > setting.Max.Value)
                {
                    throw new InvalidDataException(
                        $"Plugin setting '{setting.Id}' default is outside its range.");
                }
                if (setting.Step is > 0)
                {
                    var origin = setting.Min ?? 0;
                    var steps = (number - origin) / setting.Step.Value;
                    if (Math.Abs(steps - Math.Round(steps)) > 1e-9)
                    {
                        throw new InvalidDataException(
                            $"Plugin setting '{setting.Id}' default is not aligned to step.");
                    }
                }
                return;

            case "select" when value.ValueKind == JsonValueKind.String:
                var selected = value.GetString() ?? "";
                if (!setting.Options.Any(option =>
                        string.Equals(option.Value, selected, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        $"Plugin setting '{setting.Id}' default is not a declared option.");
                }
                return;
        }

        throw new InvalidDataException(
            $"Plugin setting '{setting.Id}' default does not match type '{setting.Type}'.");
    }
}
