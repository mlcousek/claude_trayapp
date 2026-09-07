using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Pricing;
using ClaudeTrayApp.Core.Storage;
using NSubstitute;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class AnalyticsCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly PricingTable Pricing = new(
        new DateOnly(2026, 6, 24), "USD", [new ModelPrice("claude-opus-5", PriceMatch.Prefix, 5, 25, 6.25m, 10, 0.5m)], null);

    private static IAnalyticsStore Store(params ModelTotals[] totals)
    {
        var store = Substitute.For<IAnalyticsStore>();
        store.TotalsByModel(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns(totals);
        store.TopProjects(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>()).Returns([new ProjectUsage("alpha", 1000)]);
        store.CountEvents().Returns(42);
        return store;
    }

    private static AnalyticsCalculator Calculator(IAnalyticsStore store) => new(store, () => Pricing, TimeZoneInfo.Utc);

    private static UsageSnapshot Snapshot(double percent, DateTimeOffset? resetsAt) =>
        UsageSnapshot.Empty(Now) with { Windows = [UsageWindow.Create("five_hour", percent, resetsAt)] };

    private static ModelTotals Opus(long input, int messages) => new("claude-opus-5", new TokenTotals(input, 0, 0, 0, 0, messages));

    [Fact]
    public void Sums_today_and_prices_known_models_only()
    {
        var store = Store(
            new ModelTotals("claude-opus-5", new TokenTotals(1_000_000, 100_000, 0, 0, 0, 10)),
            new ModelTotals("mystery", new TokenTotals(5, 5, 0, 0, 0, 1)));

        var analytics = Calculator(store).Compute(null, Now);

        analytics.Today.Tokens.Total.ShouldBe(1_100_010);
        analytics.Today.Cost.ShouldBe(7.5m);
        analytics.Today.HasUnknownModels.ShouldBeTrue();
        analytics.Today.ByModel[1].Cost.ShouldBeNull();
        analytics.TopProjectsToday.ShouldHaveSingleItem().Project.ShouldBe("alpha");
        analytics.EventCount.ShouldBe(42);
        store.Received().TotalsByModel(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero), Now);
    }

    [Fact]
    public void Cost_is_null_when_no_model_is_priced() =>
        Calculator(Store(new ModelTotals("mystery", new TokenTotals(5, 5, 0, 0, 0, 1)))).Compute(null, Now).Today.Cost.ShouldBeNull();

    [Fact]
    public void The_block_is_anchored_on_the_reset_and_projects_from_the_endpoint_percentage()
    {
        // Reset in 2 h: the block started 3 h ago; 60 % in 3 h is 20 %/h, so 100 % lands exactly at the reset.
        var block = Calculator(Store(Opus(3_000_000, 30))).Compute(Snapshot(60, Now.AddHours(2)), Now).CurrentBlock.ShouldNotBeNull();

        block.Start.ShouldBe(Now.AddHours(-3));
        block.End.ShouldBe(Now.AddHours(2));
        block.TokensPerHour.ShouldBe(1_000_000);
        block.PercentPerHour.ShouldBe(20);
        block.ProjectedLimitAt.ShouldBe(Now.AddHours(2));
        block.LimitBeforeReset.ShouldBeFalse();
        block.Cost.ShouldBe(15m);
    }

    [Fact]
    public void A_fast_pace_projects_the_limit_before_the_reset()
    {
        var block = Calculator(Store(Opus(10, 1))).Compute(Snapshot(80, Now.AddHours(4)), Now).CurrentBlock.ShouldNotBeNull();

        block.PercentPerHour.ShouldBe(80);
        block.ProjectedLimitAt.ShouldBe(Now.AddMinutes(15));
        block.LimitBeforeReset.ShouldBeTrue();
    }

    [Fact]
    public void An_exhausted_window_projects_now()
    {
        var block = Calculator(Store(Opus(10, 1))).Compute(Snapshot(100, Now.AddHours(1)), Now).CurrentBlock.ShouldNotBeNull();

        block.ProjectedLimitAt.ShouldBe(Now);
        block.LimitBeforeReset.ShouldBeTrue();
    }

    [Fact]
    public void Without_a_reset_time_the_block_is_the_last_five_hours_without_projection()
    {
        var block = Calculator(Store(Opus(500, 1))).Compute(Snapshot(30, null), Now).CurrentBlock.ShouldNotBeNull();

        block.Start.ShouldBe(Now.AddHours(-5));
        block.End.ShouldBe(Now);
        block.PercentPerHour.ShouldBeNull();
        block.ProjectedLimitAt.ShouldBeNull();
        block.TokensPerHour.ShouldBe(100);
    }

    [Fact]
    public void No_tokens_and_no_reset_means_no_block() =>
        Calculator(Store()).Compute(null, Now).CurrentBlock.ShouldBeNull();

    /// <summary>
    /// A synthetic zone, portable across machines and CI, that springs forward by 1 h at 2026-03-15 02:00 local
    /// (standard offset +1h, daylight offset +2h from that instant). Local midnight on 2026-03-15 is still in
    /// standard time (+1h); by the time "now" is evaluated the zone has already sprung forward to +2h.
    /// </summary>
    private static TimeZoneInfo SpringForwardZone()
    {
        var timeOfDay = new DateTime(1, 1, 1, 2, 0, 0);
        var transition = TimeZoneInfo.TransitionTime.CreateFixedDateRule(timeOfDay, 3, 15);
        var transitionBack = TimeZoneInfo.TransitionTime.CreateFixedDateRule(timeOfDay, 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            transition,
            transitionBack);
        return TimeZoneInfo.CreateCustomTimeZone("Synthetic/SpringForward", TimeSpan.FromHours(1), "Synthetic Spring-Forward", "Synthetic Standard", "Synthetic Daylight", [rule]);
    }

    [Fact]
    public void Todays_totals_include_an_event_just_after_local_midnight_across_a_spring_forward_transition()
    {
        var zone = SpringForwardZone();

        // Local midnight on the transition day is +1h (standard); "now" (08:00 local) is after the 02:00 jump,
        // so it is +2h (daylight). True UTC local midnight is therefore 2026-03-14T23:00:00Z, not 2026-03-14T22:00:00Z
        // (what reusing now's +2h offset for midnight would wrongly compute).
        var localMidnightUtc = new DateTimeOffset(2026, 3, 14, 23, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.FromHours(2)); // 08:00 local, daylight offset

        var store = Substitute.For<IAnalyticsStore>();
        store.TotalsByModel(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>()).Returns([]);
        store.TopProjects(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>()).Returns([]);
        store.CountEvents().Returns(0);

        new AnalyticsCalculator(store, () => Pricing, zone).Compute(null, now);

        // The DST-safe day boundary must be true local midnight in UTC, not now's offset applied to today's date
        // (which would be 2026-03-14T22:00:00Z - one hour too early, wrongly including an hour of yesterday).
        store.Received().TotalsByModel(localMidnightUtc, now);
        store.Received().TopProjects(localMidnightUtc, now, 3);
    }

    /// <summary>
    /// End-to-end version of the spring-forward test above against a real store: reusing "now"'s +2h (daylight)
    /// offset for midnight would compute a day boundary of 2026-03-14T22:00:00Z, one hour earlier than the true
    /// boundary of 2026-03-14T23:00:00Z (midnight is still standard time, +1h, before that day's 02:00 jump). That
    /// wrongly pulls an hour of *yesterday* (22:00-23:00Z, still 23:00-24:00 local standard time on the 14th) into
    /// "today" - the double-counting failure mode. An event seeded in exactly that disputed hour must be excluded
    /// once the boundary is computed correctly, while an event just after the true boundary is still included.
    /// </summary>
    [Fact]
    public void Todays_totals_exclude_a_pre_midnight_event_and_include_a_post_midnight_one_across_a_spring_forward()
    {
        var zone = SpringForwardZone();
        var now = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.FromHours(2)); // 08:00 UTC = 10:00 local (daylight)
        var directory = Path.Combine(Path.GetTempPath(), "ClaudeTrayApp.Tests", Guid.NewGuid().ToString("N"));
        var store = new SqliteStore(Path.Combine(directory, "history.db"));
        try
        {
            store.InsertEvents([
                // 22:30Z on the 14th: within the disputed hour, still "yesterday" in true local time. A correct
                // fix must exclude it from today's totals.
                new UsageEvent("msg-disputed", "req-disputed", new DateTimeOffset(2026, 3, 14, 22, 30, 0, TimeSpan.Zero), "claude-opus-5", null, null, 111, 0, 0, 0, 0),
                // 23:30Z on the 14th: just after the true local midnight (23:00Z). Must be included.
                new UsageEvent("msg-today", "req-today", new DateTimeOffset(2026, 3, 14, 23, 30, 0, TimeSpan.Zero), "claude-opus-5", null, null, 222, 0, 0, 0, 0),
            ]);

            var today = new AnalyticsCalculator(store, () => Pricing, zone).Compute(null, now).Today;

            today.Tokens.Input.ShouldBe(222);
            today.Tokens.Messages.ShouldBe(1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
