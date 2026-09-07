using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using ClaudeTrayApp.Core.Account;
using ClaudeTrayApp.Core.Diagnostics;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Polling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.ViewModels;

/// <summary>Everything the flyout binds to. Formatting happens here; the view only binds and draws.</summary>
public sealed partial class FlyoutViewModel : ObservableObject, IDisposable
{
    private readonly UsagePoller _poller;
    private readonly IAccountInfoSource _accountSource;
    private readonly TimeProvider _clock;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<FlyoutViewModel> _logger;
    private PollStatus _status;
    private AccountInfo _account = AccountInfo.Empty;

    [ObservableProperty]
    private string _planTierText = "Claude";

    [ObservableProperty]
    private string _accountText = string.Empty;

    [ObservableProperty]
    private bool _hasAccount;

    [ObservableProperty]
    private bool _hasEmail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskToggleLabel))]
    private bool _isEmailMasked = true;

    [ObservableProperty]
    private UsageWindowViewModel? _primary;

    [ObservableProperty]
    private bool _hasPrimary;

    [ObservableProperty]
    private bool _hasSecondary;

    [ObservableProperty]
    private bool _hasWindows;

    [ObservableProperty]
    private bool _hasNoWindows = true;

    [ObservableProperty]
    private string _unavailableText = "Percentages unavailable. Waiting for the first refresh.";

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _hasStatusMessage;

    [ObservableProperty]
    private string? _overageText;

    [ObservableProperty]
    private string _overageAmountText = string.Empty;

    [ObservableProperty]
    private double _overagePercent;

    [ObservableProperty]
    private bool _hasOverage;

    [ObservableProperty]
    private string _lastUpdatedText = "No data yet";

    [ObservableProperty]
    private string? _refreshFeedback;

    [ObservableProperty]
    private bool _hasRefreshFeedback;

    public FlyoutViewModel(
        UsagePoller poller,
        IAccountInfoSource accountSource,
        TimeProvider clock,
        Dispatcher dispatcher,
        ILogger<FlyoutViewModel> logger)
    {
        _poller = poller;
        _accountSource = accountSource;
        _clock = clock;
        _dispatcher = dispatcher;
        _logger = logger;
        _status = poller.Status;

        _poller.StatusChanged += OnStatusChanged;
        Rebuild();
    }

    /// <summary>Every window except the primary one, in endpoint order.</summary>
    public ObservableCollection<UsageWindowViewModel> SecondaryWindows { get; } = [];

    public string MaskToggleLabel => IsEmailMasked ? "Show" : "Hide";

    /// <summary>Hides the account line entirely; used when producing screenshots.</summary>
    public bool AccountHidden { get; set; }

    /// <summary>Reads the account block once. Safe to call again; failures leave the header without an email.</summary>
    public async Task LoadAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            _account = await _accountSource.ReadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Account info could not be read");
            _account = AccountInfo.Empty;
        }

        await _dispatcher.InvokeAsync(UpdateHeader);
    }

    /// <summary>Projects a poll status onto the bindable state. Must run on the UI thread.</summary>
    public void Apply(PollStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _status = status;
        if (status.State is PollState.Ok or PollState.Stale or PollState.RateLimited or PollState.Unauthenticated)
        {
            ClearFeedback();
        }

        Rebuild();
    }

    /// <summary>Re-renders time-dependent text (countdowns, "updated 3 min ago") without new data.</summary>
    public void Tick() => Rebuild();

    /// <summary>"max" becomes "Claude Max"; tier strings such as default_claude_max_5x become "Claude Max 5x".</summary>
    internal static string FormatPlan(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
        {
            return "Claude";
        }

        const string TierPrefix = "default_claude_";
        var name = tier.StartsWith(TierPrefix, StringComparison.OrdinalIgnoreCase) ? tier[TierPrefix.Length..] : tier;
        var words = name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select((word, index) => index == 0 ? Capitalize(word) : word.ToLower(CultureInfo.InvariantCulture));
        return "Claude " + string.Join(' ', words);
    }

    public void Dispose() => _poller.StatusChanged -= OnStatusChanged;

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..].ToLower(CultureInfo.InvariantCulture);

    private void Rebuild()
    {
        var now = _clock.GetUtcNow();
        var snapshot = _status.Snapshot;
        var primary = snapshot?.PrimaryWindow;

        if (primary is null)
        {
            Primary = null;
        }
        else if (Primary is { } existing && string.Equals(existing.Key, primary.Key, StringComparison.OrdinalIgnoreCase))
        {
            existing.Update(primary, now);
        }
        else
        {
            Primary = new UsageWindowViewModel(primary, now);
        }

        HasPrimary = Primary is not null;
        SyncSecondary(snapshot?.Windows.Where(w => !ReferenceEquals(w, primary)).ToList() ?? [], now);
        HasSecondary = SecondaryWindows.Count > 0;
        HasWindows = HasPrimary || HasSecondary;
        HasNoWindows = !HasWindows;

        UnavailableText = _status.Message is { Length: > 0 } message
            ? "Percentages unavailable. " + message
            : "Percentages unavailable. Waiting for the usage endpoint.";

        StatusMessage = HasWindows ? BannerFor(_status) : null;
        HasStatusMessage = StatusMessage is not null;

        if (snapshot?.Overage is { IsEnabled: true } overage)
        {
            OverageAmountText = DescribeOverageAmount(overage);
            OveragePercent = overage.UtilizationPercent ?? 0;
            OverageText = string.Create(CultureInfo.InvariantCulture, $"Extra usage: {OverageAmountText} ({Math.Round(OveragePercent)}%)");
            HasOverage = true;
        }
        else
        {
            OverageText = null;
            OverageAmountText = string.Empty;
            OveragePercent = 0;
            HasOverage = false;
        }

        LastUpdatedText = _status.LastSuccess is { } success
            ? (_status.IsStale ? "Cached, updated " : "Updated ") + RelativeTime.Format(success, now)
            : "No data yet";

        UpdateHeader();
    }

    private void SyncSecondary(List<UsageWindow> windows, DateTimeOffset now)
    {
        for (var i = SecondaryWindows.Count - 1; i >= 0; i--)
        {
            if (!windows.Any(w => string.Equals(w.Key, SecondaryWindows[i].Key, StringComparison.OrdinalIgnoreCase)))
            {
                SecondaryWindows.RemoveAt(i);
            }
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var existing = SecondaryWindows.FirstOrDefault(v => string.Equals(v.Key, window.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                SecondaryWindows.Insert(Math.Min(i, SecondaryWindows.Count), new UsageWindowViewModel(window, now));
            }
            else
            {
                existing.Update(window, now);
            }
        }
    }

    private static string? BannerFor(PollStatus status) => status.State switch
    {
        PollState.RateLimited => status.Message ?? "Rate limited by the usage endpoint. Showing the last known values.",
        PollState.Unauthenticated => status.Message ?? "Not signed in. Open Claude Code to sign in again.",
        PollState.Stale => status.Message ?? "The usage endpoint is unavailable. Showing the last known values.",
        PollState.Idle when status.Snapshot?.Source == UsageSource.Cache => "Showing cached data until the first refresh completes.",
        _ => null,
    };

    private static string DescribeOverageAmount(OverageInfo overage)
    {
        var currency = overage.Currency ?? string.Empty;
        var used = overage.UsedAmount?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
        var limit = overage.LimitAmount?.ToString("0.##", CultureInfo.InvariantCulture);
        return (limit is null ? $"{used} {currency} spent" : $"{used} of {limit} {currency}").Trim();
    }

    private void UpdateHeader()
    {
        PlanTierText = FormatPlan(_status.Snapshot?.PlanTier ?? _account.OrganizationRateLimitTier);
        if (AccountHidden)
        {
            HasEmail = false;
            AccountText = string.Empty;
            HasAccount = false;
            return;
        }

        HasEmail = _account.Email is not null;
        AccountText = _account.Email is { } email
            ? IsEmailMasked ? AccountInfo.MaskEmail(email) : email
            : _account.DisplayName ?? string.Empty;
        HasAccount = AccountText.Length > 0;
    }

    private void ShowFeedback(string? text)
    {
        RefreshFeedback = text;
        HasRefreshFeedback = !string.IsNullOrEmpty(text);
    }

    private void ClearFeedback() => ShowFeedback(null);

    partial void OnIsEmailMaskedChanged(bool value) => UpdateHeader();

    [RelayCommand]
    private void Refresh()
    {
        if (_poller.TryRequestRefresh(out var reason))
        {
            ShowFeedback("Refreshing…");
            return;
        }

        ShowFeedback(reason);
    }

    [RelayCommand]
    private void ToggleEmailMask() => IsEmailMasked = !IsEmailMasked;

    private void OnStatusChanged(object? sender, PollStatus status) => _dispatcher.BeginInvoke(() => Apply(status));
}
