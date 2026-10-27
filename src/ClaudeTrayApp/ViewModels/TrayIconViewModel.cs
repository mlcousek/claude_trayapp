using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClaudeTrayApp.Core;
using ClaudeTrayApp.Core.Diagnostics;
using ClaudeTrayApp.Core.Notifications;
using ClaudeTrayApp.Core.Polling;
using ClaudeTrayApp.Core.Settings;
using ClaudeTrayApp.Startup;
using ClaudeTrayApp.Tray;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.ViewModels;

/// <summary>A short message for the user, shown as a Windows notification from the tray icon.</summary>
public sealed record TrayNotification(string Title, string Message, bool IsWarning);

/// <summary>State behind the tray icon: what to draw, what the tooltip says, and the context-menu commands.</summary>
public sealed partial class TrayIconViewModel : ObservableObject, IDisposable
{
    /// <summary>Shell tooltips are cut at 127 characters; stay comfortably below.</summary>
    public const int TooltipLimit = 120;

    private readonly UsagePoller _poller;
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly ThresholdNotifier _notifier;
    private readonly IAutostartEntry _autostart;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly string _version;
    private readonly ILogger<TrayIconViewModel> _logger;
    private readonly AccountIdentity _account;
    private bool _loadingAutostart;

    [ObservableProperty]
    private TrayIconState _iconState = TrayIconState.Unknown;

    [ObservableProperty]
    private string _tooltip = "Claude Usage Tray: waiting for the first refresh";

    [ObservableProperty]
    private bool _startWithWindows;

    public TrayIconViewModel(
        UsagePoller poller,
        AppPaths paths,
        SettingsStore settings,
        ThresholdNotifier notifier,
        IAutostartEntry autostart,
        TimeProvider clock,
        Dispatcher dispatcher,
        Action openSettings,
        Action quit,
        string version,
        ILogger<TrayIconViewModel> logger,
        AccountIdentity? account = null)
    {
        _poller = poller;
        _paths = paths;
        _settings = settings;
        _notifier = notifier;
        _autostart = autostart;
        _clock = clock;
        _dispatcher = dispatcher;
        _openSettings = openSettings;
        _quit = quit;
        _version = version;
        _logger = logger;
        _account = account ?? AccountIdentity.Default;

        _poller.StatusChanged += OnStatusChanged;
        _settings.Changed += OnSettingsChanged;
        _account.PropertyChanged += OnAccountChanged;
        Apply(_poller.Status);
        RefreshAutostart();
    }

    /// <summary>The fixed tray icon id of a non-default account; null for the default account.</summary>
    public Guid? TrayIconId => _account.Account.TrayIconId;

    /// <summary>Raised when the user should see a short message: a refused refresh, a threshold crossed.</summary>
    public event EventHandler<TrayNotification>? NotificationRequested;

    /// <summary>Projects a poll status onto the icon state and tooltip, and announces crossed thresholds. Must run on the UI thread.</summary>
    public void Apply(PollStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var settings = _settings.Current;
        var primary = status.Snapshot?.WindowFor(settings.TrayWindow);
        var stale = status.State is PollState.Idle or PollState.Stale or PollState.RateLimited or PollState.Unauthenticated;
        var now = _clock.GetUtcNow();

        IconState = primary is null
            ? TrayIconState.Unknown with { IsStale = stale }
            : new TrayIconState(primary.UtilizationPercent, primary.Status, stale);
        Tooltip = BuildTooltip(status, now, settings.ShowInactiveWindows, _account.Label);

        if (status.State == PollState.Ok && status.Snapshot is { } snapshot && settings.Notifications.Enabled)
        {
            foreach (var alert in _notifier.Evaluate(snapshot, settings.Notifications.Thresholds))
            {
                _logger.LogInformation("Threshold notification: {Window} at {Percent}% passed {Threshold}%", alert.WindowKey, Math.Round(alert.Percent), alert.Threshold);
                NotificationRequested?.Invoke(this, new TrayNotification("Claude usage", FormatAlert(alert, now), IsWarning: true));
            }
        }
    }

    /// <summary>Re-reads the Run entry; the menu calls this when it opens because the settings window can change it too.</summary>
    public void RefreshAutostart()
    {
        _loadingAutostart = true;
        try
        {
            StartWithWindows = _autostart.IsEnabled();
        }
        finally
        {
            _loadingAutostart = false;
        }
    }

    /// <summary>"5-hour usage is at 86%, past your 80% mark. Resets in 1h 53m."</summary>
    internal static string FormatAlert(ThresholdAlert alert, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var reset = alert.ResetsAt is { } at && at > now ? " Resets in " + UsageSnapshotFormatter.FormatDuration(at - now) + "." : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{alert.WindowName} usage is at {Math.Round(alert.Percent)}%, past your {alert.Threshold}% mark.{reset}");
    }

    /// <summary>
    /// One line: the first window with its reset countdown, up to two more windows, then the state. An account
    /// started with <c>--claude-dir</c> is named after the app: "Claude usage (Work): …".
    /// </summary>
    internal static string BuildTooltip(PollStatus status, DateTimeOffset now, bool showInactiveWindows = false, string? accountLabel = null)
    {
        var name = accountLabel is null ? string.Empty : " (" + accountLabel + ")";
        string text;
        if (status.Snapshot is { } snapshot && snapshot.VisibleWindows(showInactiveWindows) is { Count: > 0 } windows)
        {
            var parts = windows.Take(3).Select((window, index) => index == 0
                ? UsageSnapshotFormatter.DescribeWindow(window, now)
                : string.Create(CultureInfo.InvariantCulture, $"{window.DisplayName} {Math.Round(window.UtilizationPercent)}%"));
            text = "Claude usage" + name + ": " + string.Join(" · ", parts) + Suffix(status.State);
        }
        else
        {
            text = "Claude Usage Tray" + name + ": " + (status.Message ?? "no data yet");
        }

        return text.Length <= TooltipLimit ? text : text[..(TooltipLimit - 1)] + "…";
    }

    public void Dispose()
    {
        _poller.StatusChanged -= OnStatusChanged;
        _settings.Changed -= OnSettingsChanged;
        _account.PropertyChanged -= OnAccountChanged;
    }

    /// <summary>The title a notification from this icon carries: "Claude usage · Work" for a non-default account.</summary>
    public string NotificationTitle(string title) => _account.Decorate(title);

    /// <summary>The letter that tells a second account's icon apart ("W" for Work); null for the default account.</summary>
    public string? Badge => _account.Badge;

    /// <summary>"Account: Work\nClaude home: C:\…\n\n" for a non-default account; empty for the default one.</summary>
    internal static string DescribeAccount(AccountIdentity account) =>
        account.Account.ClaudeHome is { } home ? $"Account: {account.Label}\nClaude home: {home}\n\n" : string.Empty;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loadingAutostart)
        {
            return;
        }

        if (!_autostart.TrySet(value, out var error))
        {
            NotificationRequested?.Invoke(this, new TrayNotification("Claude Usage Tray", error ?? "The startup entry could not be changed.", IsWarning: true));
            RefreshAutostart();
        }
    }

    private static string Suffix(PollState state) => state switch
    {
        PollState.RateLimited => " · rate limited",
        PollState.Unauthenticated => " · run the Claude Code CLI",
        PollState.Stale => " · stale",
        PollState.Idle => " · cached",
        _ => string.Empty,
    };

    [RelayCommand]
    private void Refresh()
    {
        if (_poller.TryRequestRefresh(out var reason))
        {
            _logger.LogInformation("Manual refresh requested from the tray menu");
            return;
        }

        NotificationRequested?.Invoke(this, new TrayNotification("Claude Usage Tray", reason ?? "Refresh is not possible right now.", IsWarning: false));
    }

    [RelayCommand]
    private void Settings() => _openSettings();

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo(_paths.LogsDirectory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the logs folder");
        }
    }

    [RelayCommand]
    private void About()
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"Claude Usage Tray {_version}\n\nAn unofficial, open-source tray app that shows your Claude usage limits.\nNot affiliated with or endorsed by Anthropic.\n\nhttps://github.com/mlcousek/claude_trayapp\n\n{DescribeAccount(_account)}Logs: {_paths.LogsDirectory}");
        MessageBox.Show(text, "About Claude Usage Tray", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void Quit()
    {
        _logger.LogInformation("Quit requested from the tray menu");
        _quit();
    }

    private void OnStatusChanged(object? sender, PollStatus status) => _dispatcher.BeginInvoke(() => Apply(status));

    private void OnSettingsChanged(object? sender, AppSettings settings) => _dispatcher.BeginInvoke(() => Apply(_poller.Status));

    private void OnAccountChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        Tooltip = BuildTooltip(_poller.Status, _clock.GetUtcNow(), _settings.Current.ShowInactiveWindows, _account.Label);
        OnPropertyChanged(nameof(Badge));
    });
}
