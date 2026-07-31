using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaperTodo.Plugin;

namespace PaperTodo;

internal static class PaperBodyProviderIds
{
    public const string Markdown = "builtin.markdown";
}

internal enum PaperBodyPluginKind
{
    BuiltIn,
    Native,
    Web
}

internal sealed record PaperBodyPluginDescriptor(
    string Id,
    string DisplayName,
    string Description,
    Version Version,
    int StateVersion,
    PaperBodyPluginKind Kind,
    PaperBodyCapabilities Capabilities,
    string PluginDirectory,
    string SourcePath,
    string Fingerprint,
    Type? NativePluginType = null,
    PaperBodyPluginManifest? Manifest = null);

internal sealed record PaperBodyPluginLoadIssue(
    string SourcePath,
    string Message,
    bool RestartRequired = false);

internal sealed record PaperBodyNativePluginActivation(
    IPaperBodyPlugin Plugin,
    PaperBodyPluginDescriptor Descriptor);

internal sealed class PaperBodyPluginManifest
{
    public string Kind { get; set; } = "web";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public int ApiVersion { get; set; } = 1;
    public int StateVersion { get; set; } = 1;
    public string Entry { get; set; } = "index.html";
    public string[] Capabilities { get; set; } = [];

    public string DirectoryPath { get; internal set; } = "";
    public string EntryPath { get; internal set; } = "";
}

/// <summary>
/// Discovers one fully trusted, unsandboxed native or local Web plugin from each self-contained
/// plugins/&lt;plugin-id&gt;/plugin.json folder. Native code is not hot-replaced: changed native
/// folders remain on the loaded version until restart, while Web folders reload immediately.
/// </summary>
internal sealed partial class PaperBodyPluginRegistry : IDisposable
{
    private const int SupportedPluginApiVersion = 1;
    private static readonly Regex PluginIdPattern = PluginIdRegex();
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed record LoadedNativePlugin(
        string DirectoryPath,
        string Fingerprint,
        PaperBodyPluginDescriptor Descriptor,
        NativePluginLoadContext LoadContext);

    private readonly Dictionary<string, PaperBodyPluginDescriptor> _descriptors =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedNativePlugin> _loadedNativeByDirectory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PaperBodyPluginLoadIssue> _issues = [];
    private HashSet<string> _lastChangedProviderIds = new(StringComparer.Ordinal);
    private bool _disposed;

    public PaperBodyPluginRegistry()
    {
        PluginRoot = Path.Combine(AppContext.BaseDirectory, "plugins");
        Reload(scanPluginContents: false);
    }

    public string PluginRoot { get; }

    public IReadOnlyList<PaperBodyPluginDescriptor> Descriptors =>
        _descriptors.Values
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public IReadOnlyList<PaperBodyPluginLoadIssue> Issues => _issues.ToArray();
    public IReadOnlySet<string> LastChangedProviderIds => _lastChangedProviderIds;

    public bool TryGet(string? id, out PaperBodyPluginDescriptor descriptor)
    {
        var normalized = string.IsNullOrWhiteSpace(id)
            ? PaperBodyProviderIds.Markdown
            : id.Trim();
        return _descriptors.TryGetValue(normalized, out descriptor!);
    }

    public PaperBodyNativePluginActivation CreateNativePlugin(
        PaperBodyPluginDescriptor descriptor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (descriptor.Kind != PaperBodyPluginKind.Native ||
            descriptor.Manifest == null)
        {
            throw new InvalidOperationException("The descriptor is not a native plugin.");
        }

        if (_loadedNativeByDirectory.TryGetValue(
                descriptor.PluginDirectory,
                out var loaded))
        {
            if (!string.Equals(
                    loaded.Descriptor.Id,
                    descriptor.Id,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The loaded native plugin does not match the requested descriptor.");
            }

            var pluginType = loaded.Descriptor.NativePluginType
                ?? throw new InvalidOperationException(
                    "The loaded native plugin has no factory type.");
            var plugin = (IPaperBodyPlugin?)Activator.CreateInstance(pluginType)
                ?? throw new InvalidOperationException(
                    $"Could not create native plugin {pluginType.FullName}.");
            return new PaperBodyNativePluginActivation(
                plugin,
                loaded.Descriptor);
        }

        return LoadNativePlugin(descriptor);
    }

    public void Reload() => Reload(scanPluginContents: true);

    private void Reload(bool scanPluginContents)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _descriptors.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        var next = new Dictionary<string, PaperBodyPluginDescriptor>(StringComparer.Ordinal);
        _issues.Clear();

        next[PaperBodyProviderIds.Markdown] = new PaperBodyPluginDescriptor(
            PaperBodyProviderIds.Markdown,
            Strings.Get("BodyProviderMarkdown"),
            Strings.Get("BodyProviderMarkdownDescription"),
            typeof(PaperWindow).Assembly.GetName().Version ?? new Version(1, 0),
            1,
            PaperBodyPluginKind.BuiltIn,
            PaperBodyCapabilities.TextZoom | PaperBodyCapabilities.NoteLinks,
            AppContext.BaseDirectory,
            typeof(PaperWindow).Assembly.Location,
            "builtin");

        var discoveredNativeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginDirectories = Directory.Exists(PluginRoot)
            ? EnumeratePluginDirectories()
            : Array.Empty<string>();
        foreach (var directory in pluginDirectories)
        {
            var manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var manifest = ReadManifest(manifestPath, directory);
                var kind = NormalizeKind(manifest.Kind);
                var descriptor = kind switch
                {
                    PaperBodyPluginKind.Web => LoadWebDescriptor(
                        manifest,
                        manifestPath,
                        scanPluginContents),
                    PaperBodyPluginKind.Native => LoadOrReuseNativeDescriptor(
                        manifest,
                        manifestPath,
                        discoveredNativeDirectories),
                    _ => throw new InvalidDataException("Built-in plugins cannot be loaded from disk.")
                };
                AddDescriptor(next, descriptor);
            }
            catch (Exception ex)
            {
                _issues.Add(new PaperBodyPluginLoadIssue(
                    manifestPath,
                    ex.GetBaseException().Message));
            }
        }

        foreach (var loaded in _loadedNativeByDirectory.Values)
        {
            if (discoveredNativeDirectories.Contains(loaded.DirectoryPath))
            {
                continue;
            }

            _issues.Add(new PaperBodyPluginLoadIssue(
                loaded.DirectoryPath,
                Strings.Get("PluginsNativeRemovedRestart"),
                RestartRequired: true));
            // The CLR cannot safely replace an already loaded trusted WPF plugin. Keep the
            // in-memory descriptor usable for this process; the next start will reflect deletion.
            if (!next.ContainsKey(loaded.Descriptor.Id))
            {
                next.Add(loaded.Descriptor.Id, loaded.Descriptor);
            }
        }

        ReplaceDescriptors(next, previous);
    }

    private IEnumerable<string> EnumeratePluginDirectories()
    {
        return Directory.EnumerateDirectories(PluginRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(directory =>
            {
                var name = Path.GetFileName(directory);
                return !string.IsNullOrEmpty(name) &&
                    name[0] is not '.' and not '_';
            })
            .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase);
    }

    private PaperBodyPluginManifest ReadManifest(string manifestPath, string directory)
    {
        var manifest = JsonSerializer.Deserialize<PaperBodyPluginManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions)
            ?? throw new InvalidDataException("plugin.json deserialized to null.");
        ValidatePluginId(manifest.Id);
        var id = manifest.Id.Trim();
        if (!string.Equals(Path.GetFileName(directory), id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Plugin folder name must match plugin id '{id}'.");
        }
        if (manifest.ApiVersion != SupportedPluginApiVersion)
        {
            throw new InvalidDataException(
                $"Unsupported plugin API version {manifest.ApiVersion}; expected {SupportedPluginApiVersion}.");
        }
        if (manifest.StateVersion < 1)
        {
            throw new InvalidDataException("stateVersion must be at least 1.");
        }

        manifest.DirectoryPath = Path.GetFullPath(directory);
        manifest.EntryPath = ResolveContainedPath(directory, manifest.Entry);
        if (!File.Exists(manifest.EntryPath))
        {
            throw new FileNotFoundException("Plugin entry was not found.", manifest.EntryPath);
        }
        return manifest;
    }

    private static PaperBodyPluginKind NormalizeKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "web" => PaperBodyPluginKind.Web,
            "native" => PaperBodyPluginKind.Native,
            _ => throw new InvalidDataException("plugin kind must be 'web' or 'native'.")
        };

    private PaperBodyPluginDescriptor LoadWebDescriptor(
        PaperBodyPluginManifest manifest,
        string manifestPath,
        bool scanPluginContents)
    {
        var fingerprint = scanPluginContents
            ? PluginFolderFingerprint(manifest.DirectoryPath)
            : DiscoveryFingerprint(manifestPath, manifest.EntryPath);
        return new PaperBodyPluginDescriptor(
            manifest.Id.Trim(),
            string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id.Trim() : manifest.Name.Trim(),
            manifest.Description?.Trim() ?? "",
            ParseVersion(manifest.Version),
            manifest.StateVersion,
            PaperBodyPluginKind.Web,
            ParseCapabilities(manifest.Capabilities),
            manifest.DirectoryPath,
            manifestPath,
            fingerprint,
            Manifest: manifest);
    }

    private PaperBodyPluginDescriptor LoadOrReuseNativeDescriptor(
        PaperBodyPluginManifest manifest,
        string manifestPath,
        HashSet<string> discoveredNativeDirectories)
    {
        var directory = manifest.DirectoryPath;
        discoveredNativeDirectories.Add(directory);
        if (!string.Equals(Path.GetExtension(manifest.EntryPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A native plugin entry must be a .dll file.");
        }

        if (_loadedNativeByDirectory.TryGetValue(directory, out var loaded))
        {
            var fingerprint = PluginFolderFingerprint(directory);
            if (string.Equals(loaded.Fingerprint, fingerprint, StringComparison.Ordinal) &&
                string.Equals(loaded.Descriptor.Id, manifest.Id.Trim(), StringComparison.Ordinal))
            {
                return loaded.Descriptor;
            }

            _issues.Add(new PaperBodyPluginLoadIssue(
                directory,
                Strings.Get("PluginsNativeChangedRestart"),
                RestartRequired: true));
            return loaded.Descriptor;
        }

        // Discovery stays manifest-only. Loading the assembly, reflecting its types and running
        // its constructor are deferred until a paper actually selects this provider.
        return new PaperBodyPluginDescriptor(
            manifest.Id.Trim(),
            string.IsNullOrWhiteSpace(manifest.Name)
                ? manifest.Id.Trim()
                : manifest.Name.Trim(),
            manifest.Description?.Trim() ?? "",
            ParseVersion(manifest.Version),
            manifest.StateVersion,
            PaperBodyPluginKind.Native,
            ParseCapabilities(manifest.Capabilities),
            directory,
            manifestPath,
            DiscoveryFingerprint(manifestPath, manifest.EntryPath),
            Manifest: manifest);
    }

    private PaperBodyNativePluginActivation LoadNativePlugin(
        PaperBodyPluginDescriptor discoveredDescriptor)
    {
        var manifest = discoveredDescriptor.Manifest
            ?? throw new InvalidOperationException(
                "The native plugin manifest is unavailable.");
        var directory = manifest.DirectoryPath;
        var fingerprint = PluginFolderFingerprint(directory);
        var loadContext = new NativePluginLoadContext(manifest.EntryPath);
        IPaperBodyPlugin? plugin = null;
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(manifest.EntryPath);
            var pluginTypes = GetPluginTypes(assembly, manifest.EntryPath);
            if (pluginTypes.Length != 1)
            {
                throw new InvalidDataException(
                    "A native plugin folder must contain exactly one public parameterless IPaperBodyPlugin implementation in its entry assembly.");
            }

            var pluginType = pluginTypes[0];
            plugin = (IPaperBodyPlugin?)Activator.CreateInstance(pluginType)
                ?? throw new InvalidDataException($"Could not create {pluginType.FullName}.");
            ValidatePluginId(plugin.Id);
            if (!string.Equals(plugin.Id.Trim(), manifest.Id.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Native plugin id '{plugin.Id}' does not match manifest id '{manifest.Id}'.");
            }
            if (plugin.StateVersion < 1 ||
                plugin.StateVersion != manifest.StateVersion)
            {
                throw new InvalidDataException(
                    $"Native plugin StateVersion {plugin.StateVersion} must match manifest stateVersion {manifest.StateVersion}.");
            }

            var descriptor = new PaperBodyPluginDescriptor(
                plugin.Id.Trim(),
                string.IsNullOrWhiteSpace(manifest.Name)
                    ? (string.IsNullOrWhiteSpace(plugin.DisplayName)
                        ? plugin.Id.Trim()
                        : plugin.DisplayName.Trim())
                    : manifest.Name.Trim(),
                string.IsNullOrWhiteSpace(manifest.Description)
                    ? plugin.Description?.Trim() ?? ""
                    : manifest.Description.Trim(),
                ParseVersion(manifest.Version, plugin.Version),
                plugin.StateVersion,
                PaperBodyPluginKind.Native,
                plugin.Capabilities,
                directory,
                discoveredDescriptor.SourcePath,
                fingerprint,
                NativePluginType: pluginType,
                Manifest: manifest);
            _loadedNativeByDirectory[directory] = new LoadedNativePlugin(
                directory,
                fingerprint,
                descriptor,
                loadContext);
            if (_descriptors.TryGetValue(descriptor.Id, out var current) &&
                string.Equals(
                    current.PluginDirectory,
                    directory,
                    StringComparison.OrdinalIgnoreCase))
            {
                _descriptors[descriptor.Id] = descriptor;
            }
            return new PaperBodyNativePluginActivation(plugin, descriptor);
        }
        catch
        {
            if (plugin is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
            try { loadContext.Unload(); } catch { }
            throw;
        }
    }

    private Type[] GetPluginTypes(Assembly assembly, string sourcePath)
    {
        try
        {
            return assembly.GetTypes()
                .Where(IsPluginType)
                .ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions.Where(item => item != null))
            {
                _issues.Add(new PaperBodyPluginLoadIssue(sourcePath, loaderException!.Message));
            }
            return ex.Types
                .Where(type => type != null && IsPluginType(type))
                .Cast<Type>()
                .ToArray();
        }
    }

    private static bool IsPluginType(Type type) =>
        type.IsPublic &&
        !type.IsAbstract &&
        !type.IsInterface &&
        typeof(IPaperBodyPlugin).IsAssignableFrom(type) &&
        type.GetConstructor(Type.EmptyTypes) != null;

    private void ReplaceDescriptors(
        Dictionary<string, PaperBodyPluginDescriptor> next,
        Dictionary<string, PaperBodyPluginDescriptor> previous)
    {
        _lastChangedProviderIds = ChangedProviderIds(previous, next);
        _descriptors.Clear();
        foreach (var pair in next)
        {
            _descriptors.Add(pair.Key, pair.Value);
        }
    }

    private static HashSet<string> ChangedProviderIds(
        IReadOnlyDictionary<string, PaperBodyPluginDescriptor> previous,
        IReadOnlyDictionary<string, PaperBodyPluginDescriptor> next)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in previous.Keys.Concat(next.Keys).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(id, PaperBodyProviderIds.Markdown, StringComparison.Ordinal))
            {
                continue;
            }
            if (!previous.TryGetValue(id, out var before) ||
                !next.TryGetValue(id, out var after) ||
                before.Kind != after.Kind ||
                before.StateVersion != after.StateVersion ||
                !string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal))
            {
                changed.Add(id);
            }
        }
        return changed;
    }

    private static void AddDescriptor(
        IDictionary<string, PaperBodyPluginDescriptor> target,
        PaperBodyPluginDescriptor descriptor)
    {
        if (target.ContainsKey(descriptor.Id))
        {
            throw new InvalidDataException($"Duplicate plugin id: {descriptor.Id}");
        }
        target.Add(descriptor.Id, descriptor);
    }

    private static void ValidatePluginId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !PluginIdPattern.IsMatch(id.Trim()))
        {
            throw new InvalidDataException(
                "Plugin id must contain 3-120 ASCII letters, digits, '.', '_' or '-'.");
        }
        if (string.Equals(id.Trim(), PaperBodyProviderIds.Markdown, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The built-in Markdown id is reserved.");
        }
    }

    private static string ResolveContainedPath(string directory, string? relativePath)
    {
        var root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(directory, relativePath ?? ""));
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Plugin entry must stay inside its plugin directory.");
        }
        return combined;
    }

    private static PaperBodyCapabilities ParseCapabilities(IEnumerable<string>? values)
    {
        var result = PaperBodyCapabilities.None;
        foreach (var value in values ?? [])
        {
            result |= value?.Trim().ToLowerInvariant() switch
            {
                "textzoom" => PaperBodyCapabilities.TextZoom,
                "notelinks" => PaperBodyCapabilities.NoteLinks,
                _ => PaperBodyCapabilities.None
            };
        }
        return result;
    }

    private static Version ParseVersion(string? value, Version? fallback = null) =>
        Version.TryParse(value, out var parsed)
            ? parsed
            : fallback ?? new Version(1, 0);

    private static string PluginFolderFingerprint(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => !IsRuntimePath(directory, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(new byte[] { 0 });
            using var stream = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
            hash.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string DiscoveryFingerprint(
        string manifestPath,
        string entryPath)
    {
        var manifest = new FileInfo(manifestPath);
        var entry = new FileInfo(entryPath);
        return $"discovery:{manifest.Length}:{manifest.LastWriteTimeUtc.Ticks}:" +
            $"{entry.Length}:{entry.LastWriteTimeUtc.Ticks}";
    }

    private static bool IsRuntimePath(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, ".runtime", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _descriptors.Clear();
        _issues.Clear();
        foreach (var loaded in _loadedNativeByDirectory.Values)
        {
            try { loaded.LoadContext.Unload(); } catch { }
        }
        _loadedNativeByDirectory.Clear();
        _lastChangedProviderIds.Clear();
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdRegex();

    private sealed class NativePluginLoadContext : AssemblyLoadContext
    {
        private static readonly string AbstractionsAssemblyName =
            typeof(IPaperBodyPlugin).Assembly.GetName().Name ??
            "PaperTodo.Plugin.Abstractions";

        private static readonly HashSet<string> SharedHostAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            AbstractionsAssemblyName,
            "WinRT.Runtime",
            "Microsoft.Windows.SDK.NET",
            "Microsoft.Web.WebView2.Core",
            "Microsoft.Web.WebView2.Wpf",
            "Microsoft.Web.WebView2.WinForms"
        };

        private readonly AssemblyDependencyResolver _resolver;

        public NativePluginLoadContext(string pluginAssemblyPath)
            : base($"PaperTodo.Plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null && SharedHostAssemblyNames.Contains(assemblyName.Name))
            {
                if (string.Equals(
                        assemblyName.Name,
                        AbstractionsAssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return typeof(IPaperBodyPlugin).Assembly;
                }
                return null;
            }

            var dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return dependencyPath == null
                ? null
                : LoadFromAssemblyPath(dependencyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var dependencyPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return dependencyPath == null
                ? IntPtr.Zero
                : LoadUnmanagedDllFromPath(dependencyPath);
        }
    }
}
