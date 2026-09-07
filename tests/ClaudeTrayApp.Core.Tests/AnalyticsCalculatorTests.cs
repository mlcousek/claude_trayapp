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
}
