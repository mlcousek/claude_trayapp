using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Notifications;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class ThresholdNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly int[] Thresholds = [80, 95];

    private static UsageSnapshot Snapshot(double fiveHour, DateTimeOffset? resetsAt, double sevenDay = 10) =>
        UsageSnapshot.Empty(Now) with
        {
            Windows = [UsageWindow.Create("five_hour", fiveHour, resetsAt), UsageWindow.Create("seven_day", sevenDay, Now.AddDays(3))],
        };

    [Fact]
    public void Reports_the_highest_threshold_crossed_once_per_period()
    {
        var notifier = new ThresholdNotifier();
        var reset = Now.AddHours(2);

        var first = notifier.Evaluate(Snapshot(97, reset), Thresholds);
        var again = notifier.Evaluate(Snapshot(98, reset), Thresholds);

        first.Count.ShouldBe(1);
        first[0].WindowKey.ShouldBe("five_hour");
        first[0].WindowName.ShouldBe("5-hour");
        first[0].Threshold.ShouldBe(95);
        first[0].Percent.ShouldBe(97);
        first[0].ResetsAt.ShouldBe(reset);
        again.ShouldBeEmpty();
    }

    [Fact]
    public void Each_threshold_fires_as_it_is_crossed()
    {
        var notifier = new ThresholdNotifier();
        var reset = Now.AddHours(2);

        notifier.Evaluate(Snapshot(50, reset), Thresholds).ShouldBeEmpty();
        notifier.Evaluate(Snapshot(81, reset), Thresholds).Single().Threshold.ShouldBe(80);
        notifier.Evaluate(Snapshot(90, reset), Thresholds).ShouldBeEmpty();
        notifier.Evaluate(Snapshot(96, reset), Thresholds).Single().Threshold.ShouldBe(95);
        notifier.Evaluate(Snapshot(99, reset), Thresholds).ShouldBeEmpty();
    }

    [Fact]
    public void A_new_period_alerts_again()
    {
        var notifier = new ThresholdNotifier();

        notifier.Evaluate(Snapshot(85, Now.AddHours(2)), Thresholds).Count.ShouldBe(1);
        notifier.Evaluate(Snapshot(85, Now.AddHours(2)), Thresholds).ShouldBeEmpty();
        notifier.Evaluate(Snapshot(85, Now.AddHours(7)), Thresholds).Count.ShouldBe(1);
    }

    [Fact]
    public void Windows_alert_independently()
    {
        var notifier = new ThresholdNotifier();

        var alerts = notifier.Evaluate(Snapshot(85, Now.AddHours(2), sevenDay: 96), Thresholds);

        alerts.Select(a => (a.WindowKey, a.Threshold)).ShouldBe([("five_hour", 80), ("seven_day", 95)]);
    }

    [Fact]
    public void No_thresholds_means_no_alerts_and_reset_forgets()
    {
        var notifier = new ThresholdNotifier();

        notifier.Evaluate(Snapshot(99, Now.AddHours(2)), []).ShouldBeEmpty();
        notifier.Evaluate(Snapshot(99, Now.AddHours(2)), Thresholds).Count.ShouldBe(1);
        notifier.Reset();
        notifier.Evaluate(Snapshot(99, Now.AddHours(2)), Thresholds).Count.ShouldBe(1);
    }
}
