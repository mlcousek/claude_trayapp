using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using ClaudeTrayApp.Core;
using ClaudeTrayApp.Core.Account;
using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Cache;
using ClaudeTrayApp.Core.ClaudeCode;
using ClaudeTrayApp.Core.Credentials;
using ClaudeTrayApp.Core.Diagnostics;
using ClaudeTrayApp.Core.History;
using ClaudeTrayApp.Core.Notifications;
using ClaudeTrayApp.Core.Polling;
using ClaudeTrayApp.Core.Pricing;
using ClaudeTrayApp.Core.Providers;
using ClaudeTrayApp.Core.Settings;
using ClaudeTrayApp.Core.Storage;
using ClaudeTrayApp.Hosting;
using ClaudeTrayApp.Startup;
using ClaudeTrayApp.Theming;
using ClaudeTrayApp.Tray;
using ClaudeTrayApp.ViewModels;
using ClaudeTrayApp.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace ClaudeTrayApp;

/// <summary>Composition root. Builds the generic host, wires logging and services, and owns the app lifetime.</summary>
public partial class App : Application
{
    /// <summary>Command-line switch: fetch usage once, log a redacted summary and exit. Useful for checking a setup.</summary>
    public const string ProbeSwitch = "--probe";

    /// <summary>Command-line switch followed by a folder: write tray icon contact sheets there and exit (development aid).</summary>
    public const string RenderIconsSwitch = "--render-icons";

    /// <summary>Command-line switch followed by a PNG path: open the flyout after the first poll, screenshot it and exit (development aid).</summary>
    public const string CaptureFlyoutSwitch = "--capture-flyout";

    /// <summary>Command-line switch followed by a PNG path: open the settings window, screenshot it and exit (development aid).</summary>
    public const string CaptureSettingsSwitch = "--capture-settings";

    private IHost? _host;
    private ThemeManager? _theme;
    private TrayIconController? _tray;
    private FlyoutWindow? _flyout;
    private SingleInstance? _instance;
    private SettingsCoordinator? _settings;
    private SettingsWindowHost? _settingsWindows;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var probe = e.Args.Contains(ProbeSwitch, StringComparer.OrdinalIgnoreCase);
        var renderIconsDirectory = ArgumentAfter(e.Args, RenderIconsSwitch);
        var capturing = ArgumentAfter(e.Args, CaptureFlyoutSwitch) is not null || ArgumentAfter(e.Args, CaptureSettingsSwitch) is not null;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        var paths = AppPaths.FromEnvironment();
        Directory.CreateDirectory(paths.LogsDirectory);
        Log.Logger = BuildLogger(paths);
        RegisterCrashLogging();

        // One instance per session; a second launch just asks the first to show its flyout.
        if (!probe && renderIconsDirectory is null && !capturing && !SingleInstance.TryAcquire(out _instance))
        {
            Log.Information("Another instance is already running; asked it to show the flyout and exiting");
            Log.CloseAndFlush();
            Shutdown(0);
            return;
        }

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();
            ConfigureServices(builder.Services, paths, version, headless: probe || renderIconsDirectory is not null);
            _host = builder.Build();

            // Start off the UI thread: hosted services must not inherit the dispatcher context.
            var host = _host;
            Task.Run(() => host.StartAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host failed to start");
            Shutdown(1);
            return;
        }

        Log.Information("Claude Usage Tray {Version} started; logs in {LogsDirectory}", version, paths.LogsDirectory);

        if (probe)
        {
            var exitCode = Task.Run(() => RunProbeAsync(_host.Services)).GetAwaiter().GetResult();
            Shutdown(exitCode);
            return;
        }

        try
        {
            _theme = _host.Services.GetRequiredService<ThemeManager>();
            _settings = _host.Services.GetRequiredService<SettingsCoordinator>();
            _settings.Start();
            _theme.Apply();

            if (renderIconsDirectory is not null)
            {
                var files = IconSheetRenderer.RenderSheets(renderIconsDirectory, _theme.GetTrayPalette(AppTheme.Dark), _theme.GetTrayPalette(AppTheme.Light));
                Log.Information("Rendered tray icon sheets: {Files}", string.Join(", ", files));
                Shutdown(0);
                return;
            }

            _tray = _host.Services.GetRequiredService<TrayIconController>();
            _flyout = _host.Services.GetRequiredService<FlyoutWindow>();
            _settingsWindows = _host.Services.GetRequiredService<SettingsWindowHost>();
            _flyout.Prepare();
            _tray.LeftClick += (_, _) => _flyout.Toggle();
            _instance?.ListenForActivation(() => Dispatcher.BeginInvoke(() =>
            {
                if (_shuttingDown)
                {
                    return;
                }

                Log.Information("Another launch asked for the flyout");
                _flyout?.ShowFlyout();
            }));
            SessionEnding += (_, _) => Shutdown(0);
            _ = _host.Services.GetRequiredService<FlyoutViewModel>().LoadAccountAsync(CancellationToken.None);
            Log.Information("Tray icon ready. Left-click opens the flyout; right-click for the menu");

            if (ArgumentAfter(e.Args, CaptureFlyoutSwitch) is { } capturePath)
            {
                ScheduleFlyoutCapture(capturePath);
            }

            if (ArgumentAfter(e.Args, CaptureSettingsSwitch) is { } settingsCapturePath)
            {
                ScheduleSettingsCapture(settingsCapturePath);
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "The tray icon could not be created");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        try
        {
            _settingsWindows?.Close();
            _flyout?.Close();
            _tray?.Dispose();
            _settings?.Dispose();
            _theme?.Dispose();
            _instance?.Dispose();
            if (_host is { } host)
            {
                // Unsubscribe the DI-singleton view models before the host (and the SQLite store it owns) stops, so a
                // StatusChanged/DataChanged event firing from a background thread during shutdown can never reach a
                // view model that then touches a disposed store. Only when the tray was actually set up: probe and
                // render-icons runs shut down before these singletons are ever created.
                if (_flyout is not null)
                {
                    host.Services.GetRequiredService<FlyoutViewModel>().Dispose();
                    host.Services.GetRequiredService<TrayIconViewModel>().Dispose();
                    host.Services.GetRequiredService<SettingsViewModel>().Dispose();
                }

                // Stop off the UI thread for the same reason the host is started there.
                Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
                host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shutdown was not clean");
        }
        finally
        {
            Log.Information("Claude Usage Tray exited with code {ExitCode}", e.ApplicationExitCode);
            Log.CloseAndFlush();
        }

        base.OnExit(e);
    }

    private void ConfigureServices(IServiceCollection services, AppPaths paths, string version, bool headless)
    {
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);

        // Settings are loaded before anything that depends on them; the defaults are written on first run.
        services.AddSingleton(sp =>
        {
            var store = new SettingsStore(paths.SettingsFile, sp.GetRequiredService<ILogger<SettingsStore>>());
            store.Load();
            return store;
        });
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Current.ToPollingOptions());
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<ICredentialSource>(_ => new CredentialFileSource(paths.ClaudeCredentialsFile));
        services.AddSingleton<IClaudeCodeVersionDetector, ClaudeCodeVersionDetector>();
        services.AddSingleton<OAuthUsageProvider>();
        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<OAuthUsageProvider>());
        services.AddSingleton<ISnapshotCache>(sp => new SnapshotCache(paths.CacheFile, sp.GetRequiredService<ILogger<SnapshotCache>>()));
        services.AddSingleton<UsagePoller>();

        // Local analytics: session logs into SQLite, priced from the bundled pricing.json.
        services.AddSingleton(_ => new SqliteStore(paths.DatabaseFile));
        services.AddSingleton<IAnalyticsStore>(sp => sp.GetRequiredService<SqliteStore>());
        services.AddSingleton<IHistoryStore>(sp => sp.GetRequiredService<SqliteStore>());
        services.AddSingleton(sp => new HistoryOptions { RetentionDays = sp.GetRequiredService<SettingsStore>().Current.HistoryRetentionDays });
        services.AddSingleton(sp =>
        {
            var provider = new PricingProvider(Path.Combine(AppContext.BaseDirectory, "pricing.json"), sp.GetRequiredService<ILogger<PricingProvider>>());
            provider.Reload(sp.GetRequiredService<SettingsStore>().Current.PricingFilePath);
            return provider;
        });
        services.AddSingleton(sp => new AnalyticsCalculator(sp.GetRequiredService<IAnalyticsStore>(), () => sp.GetRequiredService<PricingProvider>().Current));
        services.AddSingleton(sp => new JsonlScanner(
            paths.ClaudeProjectsDirectory,
            sp.GetRequiredService<IAnalyticsStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<JsonlScanner>>(),
            TimeSpan.FromDays(sp.GetRequiredService<HistoryOptions>().RetentionDays)));
        services.AddSingleton(sp => new LocalAnalyticsProvider(
            sp.GetRequiredService<JsonlScanner>(),
            paths.ClaudeProjectsDirectory,
            sp.GetRequiredService<ILogger<LocalAnalyticsProvider>>()));
        services.AddSingleton<HistoryRecorder>();

        services.AddSingleton<Application>(this);
        services.AddSingleton<ThemeManager>();
        services.AddSingleton<ThresholdNotifier>();
        // The Run entry points at the process itself. Assembly.Location is empty inside a single-file publish (IL3000),
        // so the fallback is the exe next to AppContext.BaseDirectory rather than the assembly.
        services.AddSingleton(sp => new AutostartManager(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ClaudeTrayApp.exe"),
            sp.GetRequiredService<ILogger<AutostartManager>>()));
        services.AddSingleton(sp => new SettingsCoordinator(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<UsagePoller>(),
            sp.GetRequiredService<HistoryOptions>(),
            sp.GetRequiredService<HistoryRecorder>(),
            sp.GetRequiredService<JsonlScanner>(),
            sp.GetRequiredService<PricingProvider>(),
            sp.GetRequiredService<ThemeManager>(),
            Dispatcher,
            sp.GetRequiredService<ILogger<SettingsCoordinator>>()));
        services.AddSingleton(sp => new SettingsWindowHost(sp));
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<AutostartManager>(),
            sp.GetRequiredService<PricingProvider>(),
            sp.GetRequiredService<UsagePoller>(),
            sp.GetRequiredService<IHistoryStore>(),
            sp.GetRequiredService<LocalAnalyticsProvider>(),
            Dispatcher,
            sp.GetRequiredService<ILogger<SettingsViewModel>>()));
        services.AddSingleton(sp => new TrayIconViewModel(
            sp.GetRequiredService<UsagePoller>(),
            paths,
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ThresholdNotifier>(),
            sp.GetRequiredService<AutostartManager>(),
            sp.GetRequiredService<TimeProvider>(),
            Dispatcher,
            () => sp.GetRequiredService<SettingsWindowHost>().Show(),
            () => Shutdown(0),
            version,
            sp.GetRequiredService<ILogger<TrayIconViewModel>>()));
        services.AddSingleton<TrayIconController>();
        services.AddSingleton<IAccountInfoSource>(_ => new AccountInfoFileSource(paths.ClaudeConfigFile));
        services.AddSingleton(sp => new FlyoutViewModel(
            sp.GetRequiredService<UsagePoller>(),
            sp.GetRequiredService<IAccountInfoSource>(),
            sp.GetRequiredService<LocalAnalyticsProvider>(),
            sp.GetRequiredService<AnalyticsCalculator>(),
            sp.GetRequiredService<IHistoryStore>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<PricingProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            Dispatcher,
            () => sp.GetRequiredService<SettingsWindowHost>().Show(),
            sp.GetRequiredService<ILogger<FlyoutViewModel>>()));
        services.AddSingleton<FlyoutWindow>();

        if (!headless)
        {
            services.AddHostedService<UsagePollerService>();
            services.AddHostedService<LocalAnalyticsService>();
            services.AddHostedService<HistoryRecorderService>();
        }
    }

    /// <summary>One fetch, one redacted summary in the log, exit code 0 on success and 2 on any failure.</summary>
    private static async Task<int> RunProbeAsync(IServiceProvider services)
    {
        var provider = services.GetRequiredService<OAuthUsageProvider>();
        provider.VerboseShapeLogging = true;
        var clock = services.GetRequiredService<TimeProvider>();

        Log.Information("Probe: fetching usage once from {Provider}", provider.Name);
        var result = await provider.FetchAsync(CancellationToken.None);

        if (result.IsSuccess && result.Snapshot is { } snapshot)
        {
            Log.Information("Probe OK: {Summary}", UsageSnapshotFormatter.Describe(snapshot, clock.GetUtcNow()));
            foreach (var window in snapshot.Windows)
            {
                Log.Information(
                    "Probe window {Key} ({Name}): {Percent}% {Status}, resets {ResetsAt}",
                    window.Key,
                    window.DisplayName,
                    window.UtilizationPercent.ToString("0.#", CultureInfo.InvariantCulture),
                    window.Status,
                    window.ResetsAt?.ToString("O", CultureInfo.InvariantCulture) ?? "n/a");
            }

            if (snapshot.Overage is { } overage)
            {
                Log.Information(
                    "Probe extra usage: enabled {Enabled}, used {Used}, limit {Limit}, currency {Currency}",
                    overage.IsEnabled,
                    overage.UsedAmount,
                    overage.LimitAmount,
                    overage.Currency ?? "n/a");
            }

            return 0;
        }

        Log.Warning("Probe failed: {Status}. {Message}", result.Status, result.Message);
        return 2;
    }

    /// <summary>Every crash path ends in the log with a redacted message, so a failure is never silent.</summary>
    private void RegisterCrashLogging()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled exception on the UI thread");
            Log.CloseAndFlush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    /// <summary>Waits for the first poll, opens the flyout, screenshots it and exits. Development aid only.</summary>
    private void ScheduleFlyoutCapture(string path)
    {
        var open = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        open.Tick += (_, _) =>
        {
            open.Stop();
            if (_host is { } host)
            {
                // Screenshots never carry the account line.
                host.Services.GetRequiredService<FlyoutViewModel>().AccountHidden = true;
            }

            _flyout?.ShowFlyout();
            var capture = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            capture.Tick += (_, _) =>
            {
                capture.Stop();
                try
                {
                    if (_flyout is not null)
                    {
                        Log.Information("Captured the flyout to {File}", Diagnostics.FlyoutCapture.Capture(_flyout, path));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Flyout capture failed");
                }

                Shutdown(0);
            };
            capture.Start();
        };
        open.Start();
    }

    /// <summary>Opens the settings window, screenshots it and exits. Development aid only.</summary>
    private void ScheduleSettingsCapture(string path)
    {
        var open = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        open.Tick += (_, _) =>
        {
            open.Stop();
            _settingsWindows?.Show();
            var capture = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            capture.Tick += (_, _) =>
            {
                capture.Stop();
                try
                {
                    if (_settingsWindows?.Current is { } window)
                    {
                        Log.Information("Captured the settings window to {File}", Diagnostics.FlyoutCapture.Capture(window, path));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Settings capture failed");
                }

                Shutdown(0);
            };
            capture.Start();
        };
        open.Start();
    }

    private static string? ArgumentAfter(string[] args, string switchName)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, switchName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static Serilog.Core.Logger BuildLogger(AppPaths paths) => new LoggerConfiguration()
        .MinimumLevel.Debug()
        .Enrich.FromLogContext()
        .WriteTo.File(
            Path.Combine(paths.LogsDirectory, "claude-tray-.log"),
            formatProvider: CultureInfo.InvariantCulture,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 5 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            shared: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
}
