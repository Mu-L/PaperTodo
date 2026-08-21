using System.IO;
using System.Text.Json;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private const double DefaultPluginMiniMaximumWidthDip = 800;
    private const double DefaultPluginMiniMaximumHeightDip = 600;

    private int _pluginMiniMaximumGeneration = -1;
    private string _pluginMiniMaximumProviderId = "";
    private string _pluginMiniMaximumFingerprint = "";
    private bool _pluginMiniMaximumDeclarationLoaded;
    private EdgeCapsulePreviewSize? _pluginMiniDeclaredMaximum;
    private bool _pluginMiniEffectiveMaximumLocked;
    private EdgeCapsulePreviewSize _pluginMiniEffectiveMaximum;

    private sealed class MiniMaximumManifestView
    {
        public MiniMaximumSizeView? MiniMaxSize { get; set; }
    }

    private sealed class MiniMaximumSizeView
    {
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private EdgeCapsulePreviewSize ResolveEdgeCapsulePreviewMaximumCapacity()
    {
        if (_paper.Type == PaperTypes.Todo)
        {
            return new EdgeCapsulePreviewSize(450, 400);
        }
        if (_paper.Type == PaperTypes.Note && IsCurrentBodyProviderMarkdown)
        {
            return new EdgeCapsulePreviewSize(460, 410);
        }

        var descriptor = CurrentPluginMiniMaximumDescriptor();
        if (descriptor == null)
        {
            return new EdgeCapsulePreviewSize(440, 280);
        }
        if (descriptor.Kind == PaperBodyPluginKind.Web &&
            string.IsNullOrWhiteSpace(descriptor.Manifest?.MiniEntry))
        {
            return new EdgeCapsulePreviewSize(
                PluginFallbackMiniMaximumWidth,
                PluginFallbackMiniHeight);
        }

        try
        {
            EnsurePluginMiniMaximumSession(descriptor);
        }
        catch
        {
            // Capacity planning must not let a broken plugin manifest break queue layout. Opening
            // the plugin preview reads the same contract again and rejects the bad declaration.
            return DefaultPluginMiniMaximum();
        }

        if (_pluginMiniDeclaredMaximum is { } declared)
        {
            return declared;
        }
        if (_pluginMiniEffectiveMaximumLocked)
        {
            return _pluginMiniEffectiveMaximum;
        }
        if (descriptor.Kind == PaperBodyPluginKind.Web &&
            descriptor.Manifest is { } manifest &&
            !string.IsNullOrWhiteSpace(manifest.MiniEntry))
        {
            return LockPluginMiniMaximum(
                new EdgeCapsulePreviewSize(
                    manifest.MiniSize?.Width ?? 320,
                    manifest.MiniSize?.Height ?? 220),
                descriptor);
        }

        // Native PreferredMiniViewSize is not invoked from queue placement. Until the first real
        // describe locks an omitted maximum, reserve the finite compatibility default.
        return DefaultPluginMiniMaximum();
    }

    private EdgeCapsulePreviewSize ValidatePluginMiniPreferredSize(
        EdgeCapsulePreviewSize preferred)
    {
        var descriptor = CurrentPluginMiniMaximumDescriptor();
        if (descriptor == null)
        {
            return preferred;
        }

        EnsurePluginMiniMaximumSession(descriptor);
        var maximum = _pluginMiniEffectiveMaximumLocked
            ? _pluginMiniEffectiveMaximum
            : LockPluginMiniMaximum(preferred, descriptor);
        if (preferred.WidthDip > maximum.WidthDip + 0.001 ||
            preferred.HeightDip > maximum.HeightDip + 0.001)
        {
            throw new InvalidOperationException(
                $"Plugin '{descriptor.Id}' requested mini size " +
                $"{preferred.WidthDip:F1}x{preferred.HeightDip:F1} DIP, exceeding its " +
                $"effective maximum {maximum.WidthDip:F1}x{maximum.HeightDip:F1} DIP.");
        }
        return preferred;
    }

    private EdgeCapsulePreviewSize LockPluginMiniMaximum(
        EdgeCapsulePreviewSize initialPreferred,
        PaperBodyPluginDescriptor descriptor)
    {
        EnsurePluginMiniMaximumSession(descriptor);
        var maximum = _pluginMiniDeclaredMaximum ?? new EdgeCapsulePreviewSize(
            Math.Max(DefaultPluginMiniMaximumWidthDip, initialPreferred.WidthDip),
            Math.Max(DefaultPluginMiniMaximumHeightDip, initialPreferred.HeightDip));
        if (initialPreferred.WidthDip > maximum.WidthDip + 0.001 ||
            initialPreferred.HeightDip > maximum.HeightDip + 0.001)
        {
            throw new InvalidOperationException(
                $"Plugin '{descriptor.Id}' initial mini size " +
                $"{initialPreferred.WidthDip:F1}x{initialPreferred.HeightDip:F1} DIP exceeds " +
                $"declared miniMaxSize {maximum.WidthDip:F1}x{maximum.HeightDip:F1} DIP.");
        }

        _pluginMiniEffectiveMaximum = maximum;
        _pluginMiniEffectiveMaximumLocked = true;
        return maximum;
    }

    private PaperBodyPluginDescriptor? CurrentPluginMiniMaximumDescriptor()
    {
        if (_paper.Type != PaperTypes.Note || IsCurrentBodyProviderMarkdown)
        {
            return null;
        }

        var providerId = NormalizeBodyProviderId(_paper.BodyProviderId);
        if (_bodyDescriptor is { Kind: not PaperBodyPluginKind.BuiltIn } current &&
            string.Equals(current.Id, providerId, StringComparison.Ordinal))
        {
            return current;
        }
        return _controller.PaperBodyPlugins.TryGet(providerId, out var descriptor) &&
               descriptor.Kind != PaperBodyPluginKind.BuiltIn
            ? descriptor
            : null;
    }

    private void EnsurePluginMiniMaximumSession(
        PaperBodyPluginDescriptor descriptor)
    {
        if (_pluginMiniMaximumGeneration != _bodySessionGeneration ||
            !string.Equals(_pluginMiniMaximumProviderId, descriptor.Id, StringComparison.Ordinal) ||
            !string.Equals(_pluginMiniMaximumFingerprint, descriptor.Fingerprint, StringComparison.Ordinal))
        {
            _pluginMiniMaximumGeneration = _bodySessionGeneration;
            _pluginMiniMaximumProviderId = descriptor.Id;
            _pluginMiniMaximumFingerprint = descriptor.Fingerprint;
            _pluginMiniMaximumDeclarationLoaded = false;
            _pluginMiniDeclaredMaximum = null;
            _pluginMiniEffectiveMaximumLocked = false;
            _pluginMiniEffectiveMaximum = default;
        }
        if (_pluginMiniMaximumDeclarationLoaded)
        {
            return;
        }

        _pluginMiniDeclaredMaximum = ReadDeclaredPluginMiniMaximum(descriptor);
        _pluginMiniMaximumDeclarationLoaded = true;
    }

    private static EdgeCapsulePreviewSize? ReadDeclaredPluginMiniMaximum(
        PaperBodyPluginDescriptor descriptor)
    {
        var view = JsonSerializer.Deserialize<MiniMaximumManifestView>(
            File.ReadAllText(descriptor.SourcePath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        if (view?.MiniMaxSize is not { } maximum)
        {
            return null;
        }
        if (!string.Equals(descriptor.ApiVersion, "2.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException("miniMaxSize requires apiVersion 2.0.");
        }
        if (!double.IsFinite(maximum.Width) ||
            !double.IsFinite(maximum.Height) ||
            maximum.Width <= 0 ||
            maximum.Height <= 0)
        {
            throw new InvalidDataException(
                "miniMaxSize width and height must be positive finite numbers.");
        }
        return new EdgeCapsulePreviewSize(maximum.Width, maximum.Height);
    }

    private static EdgeCapsulePreviewSize DefaultPluginMiniMaximum() =>
        new(DefaultPluginMiniMaximumWidthDip, DefaultPluginMiniMaximumHeightDip);
}
