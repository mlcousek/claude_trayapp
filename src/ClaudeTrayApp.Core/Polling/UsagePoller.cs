using ClaudeTrayApp.Core.Cache;
using ClaudeTrayApp.Core.Diagnostics;
using ClaudeTrayApp.Core.Providers;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Core.Polling;

/// <summary>
/// Drives the polling loop: cache on start, fetch, apply the state machine, persist, wait.
/// Manual refreshes go through the same limiter as the timer and can never bypass a backoff.
/// </summary>
public sealed class UsagePoller : IDisposable
{
    private readonly IUsageProvider _provider;
    private readonly ISnapshotCache _cache;
    private readonly PollingStateMachine _machine;
    private readonly TimeProvider _clock;
    private readonly ILogger<UsagePoller> _logger;
    private readonly SemaphoreSlim _refreshSignal = new(0);
    private readonly object _sync = new();
    private PollStatus _status = PollStatus.Initial(null);
    private bool _initialised;

    public UsagePoller(
        IUsageProvider provider,
        ISnapshotCache cache,
        PollingOptions options,
        TimeProvider clock,
        ILogger<UsagePoller> logger)
    {
        _provider = provider;
        _cache = cache;
        _machine = new PollingStateMachine(options);
        _clock = clock;
        _logger = logger;
    }

    public event EventHandler<PollStatus>? StatusChanged;

    public PollStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <summary>Loads the cached snapshot so the UI has something to show before the first fetch completes.</summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        if (_initialised)
        {
            return;
        }

        _initialised = true;
        var cached = await _cache.LoadAsync(cancellationToken);
        if (cached is not null)
        {
            Publish(PollStatus.Initial(cached));
            _logger.LogInformation("Loaded cached snapshot from {When}", cached.LastUpdated);
        }
    }

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await InitialiseAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = await FetchOnceAsync(cancellationToken);
            await WaitAsync(delay, cancellationToken);
        }
    }

    /// <summary>One fetch cycle. Returns the delay the state machine wants before the next attempt.</summary>
    public async Task<TimeSpan> FetchOnceAsync(CancellationToken cancellationToken)
    {
        Publish(PollingStateMachine.BeginFetch(Status, _clock.GetUtcNow()));

        UsageFetchResult result;
        try
        {
            result = await _provider.FetchAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Usage provider {Provider} threw", _provider.Name);
            result = UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "Unexpected error while fetching usage.");
        }

        var completedAt = _clock.GetUtcNow();
        var (next, delay) = _machine.Complete(Status, result, completedAt);

        if (result.IsSuccess && result.Snapshot is { } snapshot)
        {
            await _cache.SaveAsync(snapshot, cancellationToken);
            _logger.LogInformation("Usage updated: {Summary}", UsageSnapshotFormatter.Describe(snapshot, completedAt));
        }
        else
        {
            _logger.LogWarning("Usage fetch {Status}: {Message} Next attempt in {Delay}", result.Status, result.Message, delay);
        }

        Publish(next);
        return delay;
    }

    /// <summary>Asks for a refresh now. Returns false, with a user-facing reason, when the limiter says no.</summary>
    public bool TryRequestRefresh(out string? reason)
    {
        if (!_machine.CanRefreshNow(Status, _clock.GetUtcNow(), out reason))
        {
            return false;
        }

        _refreshSignal.Release();
        return true;
    }

    public void Dispose() => _refreshSignal.Dispose();

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Task.Delay(delay, _clock, cancellationToken);
        var refresh = _refreshSignal.WaitAsync(waitCancellation.Token);

        var finished = await Task.WhenAny(timer, refresh);
        if (finished == refresh)
        {
            _logger.LogDebug("Manual refresh requested");
            await refresh;
            return;
        }

        // The timer won: drop the pending wait so a later release is not consumed by a stale waiter.
        await waitCancellation.CancelAsync();
        await timer;
    }

    private void Publish(PollStatus status)
    {
        lock (_sync)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }
}
