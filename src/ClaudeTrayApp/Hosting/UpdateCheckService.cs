using System.Globalization;
using System.Windows.Threading;
using ClaudeTrayApp.Core.Settings;
using ClaudeTrayApp.Core.Updates;
using ClaudeTrayApp.Startup;
using ClaudeTrayApp.Tray;
using ClaudeTrayApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Hosting;

/// <summary>
/// Asks GitHub once a week whether a newer release exists and, when one does, says so once in a tray notification.
/// Never downloads or installs anything: the user decides whether to go and get it. Skipped entirely while the
/// setting is off, so a user who wants no outbound traffic beyond the usage endpoint keeps exactly that.
/// </summary>
internal sealed class UpdateCheckService : BackgroundService
{
    /// <summary>How long after start to ask, so a launch never waits on GitHub.</summary>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>How often a check is due.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);

    /// <summary>How often the service wakes to see whether a check is due yet.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);

    private readonly UpdateChecker _checker;
    private readonly UpdateCheckStateStore _state;
    private readonly SettingsStore _settings;
    private readonly UpdateNotifier _notifier;
    private readonly IServiceProvider _services;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly Func<bool>? _defaultAccountRunning;
    private readonly string? _lockFile;

    /// <param name="settings">The store holding <c>checkForUpdates</c>: the shared root settings for every account.</param>
    /// <param name="defaultAccountRunning">
    /// Set for an account started with <c>--claude-dir</c>: while the default account runs, it does the checking.
    /// Null for the default account, which always checks.
    /// </param>
    /// <param name="lockFile">
    /// The lock every account's process takes from reading the check state to saving it (<see cref="UpdateCheckLock"/>),
    /// so two instances never check at once and announce one release twice. Null skips it (tests).
    /// </param>
    public UpdateCheckService(
        UpdateChecker checker,
        UpdateCheckStateStore state,
        SettingsStore settings,
        UpdateNotifier notifier,
        IServiceProvider services,
        Dispatcher dispatcher,
        TimeProvider clock,
        ILogger<UpdateCheckService> logger,
        Func<bool>? defaultAccountRunning = null,
        string? lockFile = null)
    {
        _checker = checker;
        _state = state;
        _settings = settings;
        _notifier = notifier;
        _services = services;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = logger;
        _defaultAccountRunning = defaultAccountRunning;
        _lockFile = lockFile;
    }

    /// <summary>"Claude Usage Tray 0.2.0 is available. You are on 0.1.1."</summary>
    internal static string FormatAvailable(ReleaseInfo release, Version current)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(current);
        return string.Create(CultureInfo.InvariantCulture, $"Claude Usage Tray {release.Version} is available. You are on {current}.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, _clock, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(PollInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; nothing to report.
        }
    }

    /// <summary>One evaluation: check when due and the setting allows it, then remember what happened.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Current.CheckForUpdates)
        {
            return;
        }

        if (_defaultAccountRunning?.Invoke() == true)
        {
            _logger.LogDebug("Update check left to the default account's instance");
            return;
        }

        // Held until the state is saved: another instance waiting behind it then finds the check no longer due.
        using var gate = _lockFile is null ? null : UpdateCheckLock.TryAcquire(_lockFile);
        if (_lockFile is not null && gate is null)
        {
            _logger.LogDebug("Another instance is checking for updates right now");
            return;
        }

        var now = _clock.GetUtcNow();
        var state = _state.Load();
        if (!state.IsDue(now, CheckInterval))
        {
            return;
        }

        UpdateCheckOutcome outcome;
        try
        {
            outcome = await _checker.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A background convenience must never take the app down.
            _logger.LogDebug(ex, "Update check failed");
            outcome = UpdateCheckOutcome.Failed("The update check failed.");
        }

        _notifier.Report(outcome, now);

        var notified = state.LastNotifiedVersion;
        if (outcome is { Status: UpdateCheckStatus.UpdateAvailable, Release: { } release })
        {
            var version = release.Version.ToString();
            if (!string.Equals(notified, version, StringComparison.Ordinal))
            {
                notified = version;
                Announce(release);
            }
        }

        _state.Save(new UpdateCheckState(now, notified));
    }

    private void Announce(ReleaseInfo release)
    {
        _logger.LogInformation("Announcing update {Version}", release.Version);
        var notification = new TrayNotification("Claude Usage Tray", FormatAvailable(release, _checker.CurrentVersion), IsWarning: false);

        // The tray icon is resolved here rather than injected: it builds WPF components in its constructor, so it
        // can only be created on the STA UI thread, and the host starts its services on a background one.
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                _services.GetRequiredService<TrayIconController>().ShowNotification(notification);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "The update notification could not be shown");
            }
        });
    }
}
