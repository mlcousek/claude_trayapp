using System.Globalization;
using ClaudeTrayApp.Charts;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.ViewModels;
using Shouldly;

namespace ClaudeTrayApp.Tests;

public class FlyoutViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "Claude")]
    [InlineData("", "Claude")]
    [InlineData("max", "Claude Max")]
    [InlineData("pro", "Claude Pro")]
    [InlineData("default_claude_max_5x", "Claude Max 5x")]
    [InlineData("default_claude_pro", "Claude Pro")]
    [InlineData("team-plus", "Claude Team plus")]
    public void Formats_plan_tiers(string? tier, string expected) =>
        FlyoutViewModel.FormatPlan(tier).ShouldBe(expected);

    [Fact]
    public void Row_view_model_conveys_status_as_text_and_countdown()
    {
        var window = UsageWindow.Create("seven_day_sonnet", 93.4, Now.AddHours(26));

        var row = new UsageWindowViewModel(window, Now);

        row.Name.ShouldBe("7-day Sonnet");
        row.PercentText.ShouldBe("93%");
        row.Status.ShouldBe(UsageWindowStatus.Critical);
        row.StatusText.ShouldBe("critical");
        row.HasStatusText.ShouldBeTrue();
        row.ResetText.ShouldBe("resets in 1d 2h");
        row.AutomationText.ShouldBe("7-day Sonnet: 93% used, critical, resets in 1d 2h");
    }

    [Fact]
    public void Row_view_model_handles_ok_and_unscheduled_windows()
    {
        var row = new UsageWindowViewModel(UsageWindow.Create("five_hour", 12, null), Now);

        row.StatusText.ShouldBeEmpty();
        row.HasStatusText.ShouldBeFalse();
        row.ResetText.ShouldBe("no reset scheduled");
        row.AutomationText.ShouldBe("5-hour: 12% used, no reset scheduled");
    }

    [Fact]
    public void Row_view_model_reports_locked_windows()
    {
        var row = new UsageWindowViewModel(UsageWindow.Create("five_hour", 100, Now.AddMinutes(30), lockedReason: "limit_reached"), Now);

        row.StatusText.ShouldBe("locked");
        row.ResetText.ShouldBe("resets in 30m");
        row.StatusSentence.ShouldBe("Locked: limit reached");
    }

    [Theory]
    [InlineData(0, "Room to work")]
    [InlineData(69.9, "Room to work")]
    [InlineData(70, "Getting close")]
    [InlineData(89.9, "Getting close")]
    [InlineData(90, "Almost used up")]
    [InlineData(99.9, "Almost used up")]
    [InlineData(100, "Fully used for now")]
    public void StatusSentence_reflects_each_status_band(double percent, string expected) =>
        new UsageWindowViewModel(UsageWindow.Create("five_hour", percent, null), Now).StatusSentence.ShouldBe(expected);

    [Fact]
    public void FormatResetsAt_uses_a_short_time_within_a_day_and_day_plus_time_beyond_it()
    {
        // Built from the local offset directly so the day/24h-window branch (which the source computes purely from
        // the UTC gap) is exercised the same way regardless of the machine's own time zone.
        var offset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 7));
        var localNow = new DateTimeOffset(2026, 9, 7, 8, 0, 0, offset);
        var soon = localNow.AddHours(2).AddMinutes(50);
        var farOut = localNow.AddHours(32);

        UsageWindowViewModel.FormatResetsAt(soon, localNow)
            .ShouldBe("at " + soon.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
        UsageWindowViewModel.FormatResetsAt(farOut, localNow)
            .ShouldBe(farOut.ToLocalTime().ToString("ddd", CultureInfo.CurrentCulture) + " " + farOut.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void FormatResetsAt_is_empty_when_already_past_due_or_unscheduled()
    {
        UsageWindowViewModel.FormatResetsAt(Now.AddMinutes(-1), Now).ShouldBeEmpty();
        UsageWindowViewModel.FormatResetsAt(Now, Now).ShouldBeEmpty();
        UsageWindowViewModel.FormatResetsAt(null, Now).ShouldBeEmpty();
    }

    [Fact]
    public void SetSpark_hides_the_sparkline_with_no_data_or_fewer_than_two_points()
    {
        var row = new UsageWindowViewModel(UsageWindow.Create("five_hour", 50, null), Now);

        row.SetSpark(null);
        row.HasSpark.ShouldBeFalse();
        row.SparkPoints.ShouldBeNull();

        row.SetSpark(new SparkData(Now.AddHours(-5), Now, [new TimePoint(Now, 50)]));
        row.HasSpark.ShouldBeFalse();
        row.SparkPoints.ShouldBeNull();
    }

    [Fact]
    public void SetSpark_hides_the_sparkline_when_coverage_is_under_five_percent_of_the_period()
    {
        var row = new UsageWindowViewModel(UsageWindow.Create("five_hour", 50, null), Now);
        // 100h period; the two points span only 1h, well under the 5h (5%) floor.
        var data = new SparkData(Now.AddHours(-100), Now, [new TimePoint(Now.AddHours(-1), 40), new TimePoint(Now, 50)]);

        row.SetSpark(data);

        row.HasSpark.ShouldBeFalse();
        row.SparkPoints.ShouldBeNull();
    }

    [Fact]
    public void SetSpark_shows_the_sparkline_when_it_covers_enough_of_the_period()
    {
        var row = new UsageWindowViewModel(UsageWindow.Create("five_hour", 50, null), Now);
        var from = Now.AddHours(-10);
        var to = Now;
        // 10h period; the points span 6h, well over the 30-minute (5%) floor.
        var data = new SparkData(from, to, [new TimePoint(Now.AddHours(-6), 20), new TimePoint(Now, 50)]);

        row.SetSpark(data);

        row.HasSpark.ShouldBeTrue();
        row.SparkPoints.ShouldNotBeNull();
        row.SparkPoints!.Count.ShouldBe(2);
        row.SparkMinX.ShouldBe(ChartsViewModel.ToX(from));
        row.SparkMaxX.ShouldBe(ChartsViewModel.ToX(to));
    }

    [Fact]
    public void CoversEnough_compares_the_covered_span_against_the_five_percent_floor()
    {
        var from = Now.AddHours(-10);
        var to = Now;

        UsageWindowViewModel.CoversEnough(new SparkData(from, to, [new TimePoint(Now.AddMinutes(-20), 1), new TimePoint(Now, 2)])).ShouldBeFalse();
        UsageWindowViewModel.CoversEnough(new SparkData(from, to, [new TimePoint(Now.AddHours(-6), 1), new TimePoint(Now, 2)])).ShouldBeTrue();
    }
}
