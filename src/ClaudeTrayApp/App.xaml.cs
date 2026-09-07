using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using ClaudeTrayApp.Core;
using ClaudeTrayApp.Core.Cache;
using ClaudeTrayApp.Core.ClaudeCode;
using ClaudeTrayApp.Core.Credentials;
using ClaudeTrayApp.Core.Diagnostics;
using ClaudeTrayApp.Core.Polling;
using ClaudeTrayApp.Core.Providers;
using ClaudeTrayApp.Hosting;
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

    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var probe = e.Args.Contains(ProbeSwitch, StringComparer.OrdinalIgnoreCase);
        var paths = AppPaths.FromEnvironment();
        Directory.CreateDirectory(paths.LogsDirectory);
        Log.Logger = BuildLogger(paths);

        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();
            ConfigureServices(builder.Services, paths, probe);
            _host = builder.Build();
            _host.Start();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host failed to start");
            Shutdown(1);
            return;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        Log.Information("Claude Usage Tray {Version} started; logs in {LogsDirectory}", version, paths.LogsDirectory);

        if (probe)
        {
            var exitCode = Task.Run(() => RunProbeAsync(_host.Services)).GetAwaiter().GetResult();
            Shutdown(exitCode);
            return;
        }

        // Milestone 2 ships providers and polling; the tray icon arrives in milestone 3.
        // Until then the process exits right away instead of lingering invisibly.
        Log.Information("No UI yet (milestone 2). Run with {Switch} to test the usage endpoint. Shutting down", ProbeSwitch);
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Host did not stop cleanly");
        }
        finally
        {
            Log.CloseAndFlush();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services, AppPaths paths, bool probe)
    {
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new PollingOptions());
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<ICredentialSource>(_ => new CredentialFileSource(paths.ClaudeCredentialsFile));
        services.AddSingleton<IClaudeCodeVersionDetector, ClaudeCodeVersionDetector>();
        services.AddSingleton<OAuthUsageProvider>();
        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<OAuthUsageProvider>());
        services.AddSingleton<ISnapshotCache>(sp => new SnapshotCache(paths.CacheFile, sp.GetRequiredService<ILogger<SnapshotCache>>()));
        services.AddSingleton<UsagePoller>();

        if (!probe)
        {
            services.AddHostedService<UsagePollerService>();
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
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
}
