using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClaudeTrayApp.Core;
using ClaudeTrayApp.Core.Diagnostics;
using ClaudeTrayApp.Core.Polling;
using ClaudeTrayApp.Tray;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.ViewModels;

/// <summary>State behind the tray icon: what to draw, what the tooltip says, and the context-menu commands.</summary>
public sealed partial class TrayIconViewModel : ObservableObject, IDisposable
{
    /// <summary>Shell tooltips are cut at 127 characters; stay comfortably below.</summary>
    public const int TooltipLimit = 120;

    private readonly UsagePoller _poller;
    private readonly AppPaths _paths;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    private readonly Action _quit;
    private readonly string _version;
    private readonly ILogger<TrayIconViewModel> _logger;

    [ObservableProperty]
    private TrayIconState _iconState = TrayIconState.Unknown;

    [ObservableProperty]
    private string _tooltip = "Claude Usage Tray: waiting for the first refresh";

    public TrayIconViewModel(
        UsagePoller poller,
        AppPaths paths,
        TimeProvider clock,
        Dispatcher dispatcher,
        Action quit,
        string version,
        ILogger<TrayIconViewModel> logger)
    {
        _poller = poller;
        _paths = paths;
        _clock = clock;
        _dispatcher = dispatcher;
        _quit = quit;
        _version = version;
        _logger = logger;

        _poller.StatusChanged += OnStatusChanged;
        Apply(_poller.Status);
    }

    /// <summary>Raised when the user should see a short message (for example a refused refresh).</summary>
    public event EventHandler<string>? NotificationRequested;

    /// <summary>Projects a poll status onto the icon state and tooltip. Must run on the UI thread.</summary>
    public void Apply(PollStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var primary = status.Snapshot?.PrimaryWindow;
        var stale = status.State is PollState.Idle or PollState.Stale or PollState.RateLimited or PollState.Unauthenticated;

        IconState = primary is null
            ? TrayIconState.Unknown with { IsStale = stale }
            : new TrayIconState(primary.UtilizationPercent, primary.Status, stale);
        Tooltip = BuildTooltip(status, _clock.GetUtcNow());
    }

    /// <summary>One line: the first window with its reset countdown, up to two more windows, then the state.</summary>
    internal static string BuildTooltip(PollStatus status, DateTimeOffset now)
    {
        string text;
        if (status.Snapshot is { Windows.Count: > 0 } snapshot)
        {
            var parts = snapshot.Windows.Take(3).Select((window, index) => index == 0
                ? UsageSnapshotFormatter.DescribeWindow(window, now)
                : string.Create(CultureInfo.InvariantCulture, $"{window.DisplayName} {Math.Round(window.UtilizationPercent)}%"));
            text = "Claude usage: " + string.Join(" · ", parts) + Suffix(status.State);
        }
        else
        {
            text = "Claude Usage Tray: " + (status.Message ?? "no data yet");
        }

        return text.Length <= TooltipLimit ? text : text[..(TooltipLimit - 1)] + "…";
    }

    public void Dispose() => _poller.StatusChanged -= OnStatusChanged;

    private static string Suffix(PollState state) => state switch
    {
        PollState.RateLimited => " · rate limited",
        PollState.Unauthenticated => " · sign in with Claude Code",
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

        NotificationRequested?.Invoke(this, reason ?? "Refresh is not possible right now.");
    }

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
            $"Claude Usage Tray {_version}\n\nAn unofficial, open-source tray app that shows your Claude usage limits.\nNot affiliated with or endorsed by Anthropic.\n\nhttps://github.com/mlcousek/claude_trayapp\n\nLogs: {_paths.LogsDirectory}");
        MessageBox.Show(text, "About Claude Usage Tray", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void Quit()
    {
        _logger.LogInformation("Quit requested from the tray menu");
        _quit();
    }

    private void OnStatusChanged(object? sender, PollStatus status) => _dispatcher.BeginInvoke(() => Apply(status));
}
