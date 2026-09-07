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
    }
}
