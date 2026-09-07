using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Polling;
using ClaudeTrayApp.Core.Providers;
using ClaudeTrayApp.Core.Tests.TestSupport;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class UsagePollerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(double percent) =>
        UsageSnapshot.Empty(Now) with { Windows = [UsageWindow.Create("five_hour", percent, Now.AddHours(2))] };

    private static (UsagePoller Poller, IUsageProvider Provider, InMemorySnapshotCache Cache, FakeTimeProvider Clock, List<PollStatus> Events) Create()
    {
        var provider = Substitute.For<IUsageProvider>();
        provider.Name.Returns("fake provider");
        var cache = new InMemorySnapshotCache();
        var clock = new FakeTimeProvider(Now);
        var poller = new UsagePoller(provider, cache, new PollingOptions(), clock, new ListLogger<UsagePoller>());
        var events = new List<PollStatus>();
        poller.StatusChanged += (_, status) => events.Add(status);
        return (poller, provider, cache, clock, events);
    }

    [Fact]
    public async Task A_successful_fetch_is_cached_and_published()
    {
        var (poller, provider, cache, _, events) = Create();
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Success(Snapshot(51)));

        var delay = await poller.FetchOnceAsync(TestContext.Current.CancellationToken);

        delay.ShouldBe(PollingOptions.DefaultInterval);
        poller.Status.State.ShouldBe(PollState.Ok);
        poller.Status.Snapshot.ShouldNotBeNull().Windows[0].UtilizationPercent.ShouldBe(51);
        cache.Saves.ShouldBe(1);
        events.Select(e => e.State).ShouldBe([PollState.Fetching, PollState.Ok]);
    }

    [Fact]
    public async Task Starts_from_the_cache_and_keeps_it_when_the_endpoint_fails()
    {
        var (poller, provider, cache, _, _) = Create();
        cache.Stored = Snapshot(33);
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "offline"));

        await poller.InitialiseAsync(TestContext.Current.CancellationToken);
        poller.Status.State.ShouldBe(PollState.Idle);
        poller.Status.IsStale.ShouldBeTrue();
        poller.Status.Snapshot.ShouldNotBeNull().Source.ShouldBe(UsageSource.Cache);

        await poller.FetchOnceAsync(TestContext.Current.CancellationToken);

        poller.Status.State.ShouldBe(PollState.Stale);
        poller.Status.Snapshot.ShouldNotBeNull().Windows[0].UtilizationPercent.ShouldBe(33);
        poller.Status.Message.ShouldBe("offline");
        cache.Saves.ShouldBe(0);
    }

    [Fact]
    public async Task A_throwing_provider_does_not_crash_the_loop()
    {
        var (poller, provider, _, _, _) = Create();
        provider.FetchAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var delay = await poller.FetchOnceAsync(TestContext.Current.CancellationToken);

        poller.Status.State.ShouldBe(PollState.Stale);
        delay.ShouldBe(PollingOptions.DefaultInterval);
    }

    [Fact]
    public async Task Manual_refresh_is_debounced_after_an_attempt()
    {
        var (poller, provider, _, clock, _) = Create();
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Success(Snapshot(1)));
        await poller.FetchOnceAsync(TestContext.Current.CancellationToken);

        poller.TryRequestRefresh(out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("wait");

        clock.Advance(TimeSpan.FromSeconds(61));
        poller.TryRequestRefresh(out reason).ShouldBeTrue();
        reason.ShouldBeNull();
    }

    [Fact]
    public async Task The_loop_stops_promptly_when_cancelled_while_waiting()
    {
        var (poller, provider, _, _, _) = Create();
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(UsageFetchResult.Success(Snapshot(1)));
        using var cancellation = new CancellationTokenSource();

        var loop = poller.RunAsync(cancellation.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        loop.IsCompleted.ShouldBeFalse();

        await cancellation.CancelAsync();
        var finished = await Task.WhenAny(loop, Task.Delay(2000, TestContext.Current.CancellationToken));

        finished.ShouldBe(loop);
        await Should.ThrowAsync<OperationCanceledException>(() => loop);
    }

    [Fact]
    public async Task Cancellation_propagates_out_of_a_fetch()
    {
        var (poller, provider, _, _, _) = Create();
        using var cancellation = new CancellationTokenSource();
        provider.FetchAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await cancellation.CancelAsync();
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return UsageFetchResult.Success(Snapshot(1));
        });

        await Should.ThrowAsync<OperationCanceledException>(() => poller.FetchOnceAsync(cancellation.Token));
    }
}
