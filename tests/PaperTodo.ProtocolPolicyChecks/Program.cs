using System.IO;
using System.Reflection;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var host = Assembly.Load("PaperTodo");
            CheckSingleHotkeyAuthority(host);
            CheckRuntimeSlotAuthority(host);
            CheckCapabilityNormalization(host);
            CheckProtocolBoundaries(host);
            CheckSharedWebInfrastructure(host);
            Console.WriteLine("PaperTodo protocol policy checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckSingleHotkeyAuthority(Assembly host)
    {
        var managerType = RequireType(host, "PaperTodo.GlobalHotkeyManager");
        var brokerType = RequireType(host, "PaperTodo.GlobalHotkeyBroker");
        Assert(
            managerType.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
                .All(field => field.FieldType.FullName != "System.Windows.Interop.HwndSource"),
            "GlobalHotkeyManager must not own a native HwndSource; the broker is the single authority.");
        Assert(
            brokerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Any(field => field.FieldType.FullName == "System.Windows.Interop.HwndSource"),
            "GlobalHotkeyBroker must own the process-level native hotkey window.");

        var tryApply = managerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "TryApply" &&
                method.GetParameters().Length == 6);
        var suspend = managerType.GetMethod(
            "Suspend",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GlobalHotkeyManager.Suspend was not found.");

        var ownerA = Activator.CreateInstance(managerType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create hotkey owner A.");
        var ownerB = Activator.CreateInstance(managerType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create hotkey owner B.");

        try
        {
            const string gesture = "Ctrl+Alt+Shift+U";
            Assert(
                ApplyReservation(tryApply, ownerA, "a", gesture),
                "An inactive configured command must be reservable without RegisterHotKey.");

            suspend.Invoke(ownerA, null);
            Assert(
                !ApplyReservation(tryApply, ownerB, "b", gesture),
                "Suspending an owner must release native registration only, not its configured reservation.");

            ((IDisposable)ownerA).Dispose();
            Assert(
                ApplyReservation(tryApply, ownerB, "b", gesture),
                "Removing an owner must release its configured reservation.");
        }
        finally
        {
            try { ((IDisposable)ownerA).Dispose(); } catch { }
            try { ((IDisposable)ownerB).Dispose(); } catch { }
        }
    }

    private static bool ApplyReservation(
        MethodInfo tryApply,
        object manager,
        string commandId,
        string gesture)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [commandId] = gesture
        };
        var failureType = tryApply.GetParameters()[5].ParameterType.GetElementType()
            ?? throw new InvalidOperationException("Could not resolve hotkey failure enum.");
        object?[] args =
        [
            bindings,
            Array.Empty<string>(),
            new[] { commandId },
            false,
            null,
            Activator.CreateInstance(failureType)
        ];
        return (bool)(tryApply.Invoke(manager, args) ?? false);
    }

    private static void CheckRuntimeSlotAuthority(Assembly host)
    {
        var controller = RequireType(host, "PaperTodo.AppController");
        Assert(
            controller.GetNestedType("PluginAppRuntimeSlot", BindingFlags.NonPublic) != null,
            "Plugin app runtime must use one provider slot state object.");
        Assert(
            controller.GetField("_pluginAppRuntimeSlots", BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Plugin app runtime slot dictionary was not found.");

        var obsoleteParallelState = new[]
        {
            "_pluginAppRuntimes",
            "_pluginAppRuntimeStarts",
            "_pluginAppRuntimeStartFailures",
            "_pluginAppRuntimeStartFailureCounts",
            "_pluginAppRuntimeRetryTokens",
            "_pluginAppRuntimeRestartRequests"
        };
        foreach (var fieldName in obsoleteParallelState)
        {
            Assert(
                controller.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic) == null,
                $"Obsolete parallel app-runtime state remains: {fieldName}");
        }
    }

    private static void CheckCapabilityNormalization(Assembly host)
    {
        var registry = RequireType(host, "PaperTodo.PaperBodyPluginRegistry");
        var manifestType = RequireType(host, "PaperTodo.PaperBodyPluginManifest");
        var normalize = registry.GetMethod(
            "NormalizeProtocolFeatures",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NormalizeProtocolFeatures was not found.");
        var capabilities = manifestType.GetProperty("Capabilities")
            ?? throw new InvalidOperationException("Manifest Capabilities property was not found.");

        var typoManifest = Activator.CreateInstance(manifestType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create plugin manifest.");
        capabilities.SetValue(typoManifest, new[] { "appRunime" });
        try
        {
            normalize.Invoke(null, new[] { typoManifest });
            throw new InvalidOperationException("Unknown capability typo was silently accepted.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        {
        }

        var canonicalManifest = Activator.CreateInstance(manifestType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create canonical plugin manifest.");
        capabilities.SetValue(
            canonicalManifest,
            new[] { " APPRUNTIME ", "textzoom", "noteLinks", "appRuntime" });
        normalize.Invoke(null, new[] { canonicalManifest });
        var values = (string[]?)capabilities.GetValue(canonicalManifest) ?? [];
        Assert(values.SequenceEqual(new[] { "appRuntime", "textZoom", "noteLinks" }),
            "Capability normalization did not produce one canonical representation.");
    }

    private static void CheckProtocolBoundaries(Assembly host)
    {
        var hostApi = RequireType(host, "PaperTodo.PaperBodyPluginHostApi");
        Assert(
            hostApi.GetMethod(
                "EnsurePresentationProtocol",
                BindingFlags.Instance | BindingFlags.NonPublic) != null,
            "Own-paper presentation lacks an explicit protocol-version gate.");
    }

    private static void CheckSharedWebInfrastructure(Assembly host)
    {
        var infrastructure = RequireType(host, "PaperTodo.WebPluginRuntimeInfrastructure");
        var appRuntime = RequireType(host, "PaperTodo.WebPluginAppRuntime");
        Assert(
            infrastructure.GetProperty(
                "JsonOptions",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null,
            "Shared Web runtime serialization policy was not found.");
        Assert(
            appRuntime.GetField(
                "JsonOptions",
                BindingFlags.Static | BindingFlags.NonPublic) == null,
            "WebPluginAppRuntime still owns a duplicate JSON bridge policy.");
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true)
        ?? throw new InvalidOperationException($"Type was not found: {name}");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
