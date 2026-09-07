using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using ClaudeTrayApp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace ClaudeTrayApp;

/// <summary>Composition root. Builds the generic host, wires logging and owns the app lifetime.</summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = AppPaths.FromEnvironment();
        Directory.CreateDirectory(paths.LogsDirectory);
        Log.Logger = BuildLogger(paths);

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();
            builder.Services.AddSingleton(paths);
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

        // Milestone 1 ships the composition root only. The tray icon arrives in milestone 3;
        // until then the process exits right away instead of lingering invisibly.
        Log.Information("No UI yet (milestone 1 scaffold); shutting down");
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
