using System.Globalization;
using ClaudeTrayApp.Core.Diagnostics;
using ClaudeTrayApp.Core.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeTrayApp.ViewModels;

/// <summary>One usage window as the flyout shows it. Status is conveyed as words as well as colour.</summary>
public sealed partial class UsageWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _percentText = string.Empty;

    [ObservableProperty]
    private UsageWindowStatus _status;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _hasStatusText;

    [ObservableProperty]
    private string _statusSentence = string.Empty;

    [ObservableProperty]
    private string _resetText = string.Empty;

    [ObservableProperty]
    private string _resetsAtText = string.Empty;

    [ObservableProperty]
    private string _automationText = string.Empty;

    public UsageWindowViewModel(UsageWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        Key = window.Key;
        Update(window, now);
    }

    public string Key { get; }

    public void Update(UsageWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);
        Name = window.DisplayName;
        Percent = window.UtilizationPercent;
        PercentText = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(window.UtilizationPercent)}%");
        Status = window.Status;

        StatusText = window.IsLocked
            ? "locked"
            : window.Status switch
            {
                UsageWindowStatus.Warning => "high",
                UsageWindowStatus.Critical => "critical",
                UsageWindowStatus.Exhausted => "full",
                _ => string.Empty,
            };
        HasStatusText = StatusText.Length > 0;

        StatusSentence = window.IsLocked
            ? "Locked: " + window.LockedReason!.Replace('_', ' ')
            : window.Status switch
            {
                UsageWindowStatus.Warning => "Getting close",
                UsageWindowStatus.Critical => "Almost used up",
                UsageWindowStatus.Exhausted => "Fully used for now",
                _ => "Room to work",
            };

        ResetText = window.TimeUntilReset(now) is { } left
            ? "resets in " + UsageSnapshotFormatter.FormatDuration(left)
            : window.ResetsAt is null ? "no reset scheduled" : "reset due";
        ResetsAtText = FormatResetsAt(window.ResetsAt, now);

        AutomationText = HasStatusText
            ? $"{Name}: {PercentText} used, {StatusText}, {ResetText}"
            : $"{Name}: {PercentText} used, {ResetText}";
    }

    /// <summary>"at 10:50" within a day, "Fri 16:00" beyond it, in the user's local time and format.</summary>
    internal static string FormatResetsAt(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } at || at <= now)
        {
            return string.Empty;
        }

        var local = at.ToLocalTime();
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        return at - now < TimeSpan.FromHours(24)
            ? "at " + time
            : local.ToString("ddd", CultureInfo.CurrentCulture) + " " + time;
    }
}
