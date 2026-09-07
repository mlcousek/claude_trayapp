using ClaudeTrayApp.Charts;
using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Pricing;
using ClaudeTrayApp.Core.Storage;
using NSubstitute;
using Shouldly;

namespace ClaudeTrayApp.Tests;

public class ChartDataLoaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Known_windows_come_first_then_unknown_keys_alphabetically() =>
        ChartDataLoader.OrderKeys(["tangelo", "seven_day_sonnet", "amber_ladder", "five_hour", "seven_day"])
            .ShouldBe(["five_hour", "seven_day", "seven_day_sonnet", "amber_ladder", "tangelo"]);

    [Fact]
    public void Sparkline_ranges_follow_the_window()
    {
        var block = new BlockAnalytics(Now.AddHours(-2), Now.AddHours(3), TimeSpan.FromHours(2), TokenTotals.Zero, null, 0, null, null, false);

        ChartDataLoader.SparkRange("five_hour", block, Now).ShouldBe((Now.AddHours(-2), Now.AddHours(3)));
        ChartDataLoader.SparkRange("five_hour", null, Now).ShouldBe((Now.AddHours(-5), Now));
        ChartDataLoader.SparkRange("seven_day_opus", block, Now).ShouldBe((Now.AddDays(-7), Now));
        ChartDataLoader.SparkRange("nimbus_quill", block, Now).ShouldBe((Now.AddHours(-24), Now));
    }

    [Fact]
    public void Flat_codename_windows_stay_out_of_the_history_chart_unless_asked()
    {
        var history = Substitute.For<IHistoryStore>();
        history.GetWindowKeys().Returns(["five_hour", "nimbus_quill", "tangelo"]);
        history.GetSeries("five_hour", Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns([new HistoryPoint(Now.AddHours(-1), 0, null), new HistoryPoint(Now, 0, null)]);
        history.GetSeries("nimbus_quill", Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns([new HistoryPoint(Now.AddHours(-1), 0, null), new HistoryPoint(Now, 0, null)]);
        history.GetSeries("tangelo", Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns([new HistoryPoint(Now.AddHours(-1), 0, null), new HistoryPoint(Now, 12, null)]);
        var store = Substitute.For<IAnalyticsStore>();
        store.DailyTotals(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>()).Returns([]);
        var loader = new ChartDataLoader(history, new AnalyticsCalculator(store, () => PricingTable.Empty, TimeZoneInfo.Utc), TimeZoneInfo.Utc);

        loader.Load(null, null, 24, Now).History.Series.Select(s => s.Key).ShouldBe(["five_hour", "tangelo"]);
        loader.Load(null, null, 24, Now, showInactiveWindows: true).History.Series.Select(s => s.Key).ShouldBe(["five_hour", "nimbus_quill", "tangelo"]);
    }

    [Fact]
    public void Load_queries_each_window_and_builds_every_chart()
    {
        var history = Substitute.For<IHistoryStore>();
        history.GetWindowKeys().Returns(["seven_day", "five_hour"]);
        history.GetSeries(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>())
            .Returns(_ => new List<HistoryPoint> { new(Now.AddHours(-1), 10, null), new(Now.AddMinutes(-5), 30, null) });
        var store = Substitute.For<IAnalyticsStore>();
        store.DailyTotals(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>())
            .Returns([new DailyModelTotals(new DateOnly(2026, 9, 7), "claude-fable-5-1", new TokenTotals(50, 0, 0, 0, 0, 1))]);
        var calculator = new AnalyticsCalculator(store, () => PricingTable.Empty, TimeZoneInfo.Utc);
        var snapshot = new UsageSnapshot(
            [UsageWindow.Create("five_hour", 35, Now.AddHours(3)), UsageWindow.Create("seven_day", 12, Now.AddDays(2))],
            "max",
            null,
            null,
            Now,
            UsageSource.Live);
        var block = new BlockAnalytics(Now.AddHours(-2), Now.AddHours(3), TimeSpan.FromHours(2), TokenTotals.Zero, null, 0, 10, Now.AddHours(6), false);

        var bundle = new ChartDataLoader(history, calculator, TimeZoneInfo.Utc).Load(snapshot, block, 24, Now);

        bundle.History.Series.Select(s => s.Key).ShouldBe(["five_hour", "seven_day"]);
        bundle.History.Series[0].Name.ShouldBe("5-hour");
        bundle.Block.ShouldNotBeNull();
        bundle.Block.Recorded[^1].ShouldBe(new TimePoint(Now, 35));
        bundle.Block.Projection.Count.ShouldBe(2);
        bundle.Daily.HasData.ShouldBeTrue();
        bundle.Sparklines.Keys.ShouldBe(["five_hour", "seven_day"], ignoreOrder: true);
        bundle.Sparklines["five_hour"].Points.ShouldBeSameAs(bundle.Block.Recorded);
        bundle.Sparklines["seven_day"].From.ShouldBe(Now.AddDays(-7));
        history.Received(1).GetSeries("seven_day", Now.AddHours(-24), Now);
        history.Received(1).GetSeries("seven_day", Now.AddDays(-7), Now);
        history.Received(1).GetSeries("five_hour", Now.AddHours(-24), Now);
        history.Received(1).GetSeries("five_hour", block.Start, Now);
    }
}
