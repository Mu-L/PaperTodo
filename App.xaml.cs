using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;

namespace PaperTodo;

public partial class App : Application
{
    private const long MaxCrashLogBytes = 100 * 1024;
    private static readonly HashSet<string> SharedDesktopRuntimeAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Windows.Forms",
        "System.Drawing",
        "System.Drawing.Common",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "WindowsFormsIntegration",
        "System.Xaml",
        "UIAutomationTypes",
        "UIAutomationProvider",
        "UIAutomationClient",
        "ReachFramework",
        "DirectWriteForwarder",
        "System.Windows.Controls.Ribbon",
        "Microsoft.VisualBasic.Forms"
    };
    private readonly object _singleInstanceCommandGate = new();
    private readonly Queue<IReadOnlyList<string>> _pendingSingleInstanceCommands = new();
    private AppController? _controller;
    private bool _singleInstanceCommandsReady;
    private SingleInstanceHelper? _singleInstance;
    private int _handlingGlobalException;

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupCommand = StartupCommand.Parse(e.Args);
        ApplyStartupCultureOverride(startupCommand.DefaultLanguage);

        // Register global unhandled exception handlers
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        _singleInstance = new SingleInstanceHelper("PaperTodo-SingleInstance-Mutex", "PaperTodo-SingleInstance-Activate");
        if (!_singleInstance.TryAcquire())
        {
            _singleInstance.SignalPrimaryInstance(e.Args);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            Environment.Exit(0);
            return;
        }

        // Listen as soon as this process owns the mutex. Commands received while
        // the controller is loading stay queued until startup is fully complete.
        _singleInstance.StartListener(HandleSingleInstanceCommand);

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            _controller = new AppController();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show(
                Strings.Format("AppStartupFailureMessage", ex.Message),
                Strings.Get("AppStartupFailureTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            _singleInstance?.Dispose();
            _singleInstance = null;

            Shutdown();
            return;
        }

        if (startupCommand.Kind == StartupCommandKind.Exit)
        {
            _controller.ExecuteStartupCommand(startupCommand);
            return;
        }

        SessionEnding += (s, args) => _controller?.Exit();
        _controller.Start(createDefaultPaper: !startupCommand.CreatesPaper);
        _controller.ExecuteStartupCommand(startupCommand);
        CompleteSingleInstanceStartup();
    }

    private void HandleSingleInstanceCommand(IReadOnlyList<string> args)
    {
        lock (_singleInstanceCommandGate)
        {
            if (!_singleInstanceCommandsReady)
            {
                _pendingSingleInstanceCommands.Enqueue(new List<string>(args));
                return;
            }
        }

        DispatchSingleInstanceCommand(args);
    }

    private void CompleteSingleInstanceStartup()
    {
        while (true)
        {
            IReadOnlyList<string> args;
            lock (_singleInstanceCommandGate)
            {
                if (_pendingSingleInstanceCommands.Count == 0)
                {
                    _singleInstanceCommandsReady = true;
                    return;
                }

                args = _pendingSingleInstanceCommands.Dequeue();
            }

            ExecuteSingleInstanceCommand(args);
        }
    }

    private void DispatchSingleInstanceCommand(IReadOnlyList<string> args)
    {
        try
        {
            Dispatcher.Invoke(() => ExecuteSingleInstanceCommand(args));
        }
        catch (InvalidOperationException)
        {
            // The application is already shutting down.
        }
    }

    private void ExecuteSingleInstanceCommand(IReadOnlyList<string> args)
    {
        var command = StartupCommand.Parse(args, StartupCommandKind.Show);
        _controller?.ExecuteStartupCommand(command);
    }

    private static void ApplyStartupCultureOverride(string? defaultLanguage)
    {
        if (TryResolveStartupCulture(defaultLanguage, out var startupCulture))
        {
            ApplyCulture(startupCulture);
            return;
        }

#if PAPERTODO_DEFAULT_ENGLISH
        ApplyCulture(CultureInfo.GetCultureInfo("en-US"));
#endif
    }

    private static bool TryResolveStartupCulture(string? language, out CultureInfo culture)
    {
        culture = null!;
        var value = (language ?? "").Trim().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var requested = CultureInfo.GetCultureInfo(value);
            if (requested.TwoLetterISOLanguageName is not ("zh" or "en" or "ja" or "ko"))
            {
                return false;
            }

            culture = requested.IsNeutralCulture
                ? CultureInfo.GetCultureInfo(requested.TwoLetterISOLanguageName switch
                {
                    "zh" => "zh-CN",
                    "ja" => "ja-JP",
                    "ko" => "ko-KR",
                    _ => "en-US"
                })
                : requested;
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs ev)
    {
        ev.Handled = true;
        HandleGlobalException(ev.Exception);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs ev)
    {
        if (ev.ExceptionObject is Exception ex)
        {
            HandleGlobalException(ex);
        }
    }

    private void HandleGlobalException(Exception ex)
    {
        if (Interlocked.Exchange(ref _handlingGlobalException, 1) != 0)
        {
            return;
        }

        var isDesktopRuntimeLoadFailure = IsSharedDotNetRuntimeLoadFailure(ex);
        WriteCrashLog(ex);

        var recoverySaved = false;
        try
        {
            var controller = AppController.Current;
            if (controller != null && controller.State != null)
            {
                try
                {
                    controller.CommitPendingNoteContentsForSave();
                }
                catch
                {
                    // Recovery should still preserve the last committed model if a live editor is broken.
                }

                var store = new StateStore();
                var recoveryPath = Path.Combine(AppContext.BaseDirectory, "data.crash_recovery.json");
                var json = store.SerializeState(controller.State);
                File.WriteAllText(recoveryPath, json);
                recoverySaved = true;
            }
        }
        catch
        {
            // Ignore exception when attempting recovery backup during crash
        }

        try
        {
            var messageKey = isDesktopRuntimeLoadFailure
                ? recoverySaved
                    ? "AppDesktopRuntimeLoadFailureMessage"
                    : "AppDesktopRuntimeLoadFailureRecoveryFailedMessage"
                : recoverySaved
                    ? "AppUnhandledExceptionMessage"
                    : "AppUnhandledExceptionRecoveryFailedMessage";
            var titleKey = isDesktopRuntimeLoadFailure
                ? "AppDesktopRuntimeLoadFailureTitle"
                : "AppUnhandledExceptionTitle";

            MessageBox.Show(
                Strings.Format(messageKey, ex.Message),
                Strings.Get(titleKey),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Ignore if GUI popup fails during crash
        }

        Environment.Exit(-1);
    }

    private static bool IsSharedDotNetRuntimeLoadFailure(Exception? ex)
    {
        var pending = new Stack<Exception?>();
        pending.Push(ex);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            while (current != null)
            {
                if (current is ReflectionTypeLoadException reflectionLoad)
                {
                    foreach (var loader in reflectionLoad.LoaderExceptions)
                    {
                        pending.Push(loader);
                    }
                }

                if (current is FileNotFoundException fileNotFound &&
                    IsSharedDesktopRuntimeAssembly(fileNotFound.FileName))
                {
                    return true;
                }

                current = current.InnerException;
            }
        }

        return false;
    }

    private static bool IsSharedDesktopRuntimeAssembly(string? assemblyDisplayName)
    {
        if (string.IsNullOrWhiteSpace(assemblyDisplayName))
        {
            return false;
        }

        string? simpleName;
        try
        {
            simpleName = new AssemblyName(assemblyDisplayName).Name;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return simpleName != null && SharedDesktopRuntimeAssemblies.Contains(simpleName);
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "PaperTodo.crash.log");
            TrimCrashLog(logPath);
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures during crash handling.
        }
    }

    private static void TrimCrashLog(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        var info = new FileInfo(logPath);
        if (info.Length <= MaxCrashLogBytes)
        {
            return;
        }

        const int keepBytes = 80 * 1024;
        using var stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytesToRead = (int)Math.Min(keepBytes, stream.Length);
        stream.Seek(-bytesToRead, SeekOrigin.End);

        var buffer = new byte[bytesToRead];
        _ = stream.Read(buffer, 0, bytesToRead);

        var marker = $"[Crash log trimmed to last {keepBytes / 1024} KB at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}]{Environment.NewLine}";
        File.WriteAllText(logPath, marker);
        File.AppendAllText(logPath, System.Text.Encoding.UTF8.GetString(buffer));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _controller?.Dispose();
        base.OnExit(e);
    }
}
