using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Polling;
using ClaudeTrayApp.ViewModels;
using Shouldly;

namespace ClaudeTrayApp.Tests;

public class TrayIconViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot() => UsageSnapshot.Empty(Now) with
    {
        Windows =
        [
            UsageWindow.Create("five_hour", 86, Now.AddHours(1).AddMinutes(53)),
            UsageWindow.Create("seven_day", 9.4, Now.AddDays(4)),
            UsageWindow.Create("nimbus_quill", 0, null),
            UsageWindow.Create("extra_window", 50, null),
        ],
    };

    private static PollStatus Status(PollState state) =>
        new(state, Snapshot(), Now, Now, Now.AddMinutes(5), null, 0);

    [Fact]
    public void Tooltip_lists_the_first_three_visible_windows_with_one_countdown()
    {
        TrayIconViewModel.BuildTooltip(Status(PollState.Ok), Now)
            .ShouldBe("Claude usage: 5-hour 86% (resets in 1h 53m) · 7-day 9% · Extra window 50%");
    }

    [Fact]
    public void Tooltip_includes_inactive_codename_windows_only_when_asked()
    {
        TrayIconViewModel.BuildTooltip(Status(PollState.Ok), Now, showInactiveWindows: true)
            .ShouldBe("Claude usage: 5-hour 86% (resets in 1h 53m) · 7-day 9% · Nimbus quill 0%");
    }

    [Theory]
    [InlineData(PollState.RateLimited, "rate limited")]
    [InlineData(PollState.Unauthenticated, "sign in with Claude Code")]
    [InlineData(PollState.Stale, "stale")]
    [InlineData(PollState.Idle, "cached")]
    public void Tooltip_names_the_degraded_state(PollState state, string expected) =>
        TrayIconViewModel.BuildTooltip(Status(state), Now).ShouldEndWith(" · " + expected);

    [Fact]
    public void Tooltip_falls_back_to_the_status_message_without_data()
    {
        var status = new PollStatus(PollState.Unauthenticated, null, null, Now, null, "Sign in with Claude Code first.", 0);

        TrayIconViewModel.BuildTooltip(status, Now).ShouldBe("Claude Usage Tray: Sign in with Claude Code first.");
    }

    [Fact]
    public void Tooltip_never_exceeds_the_shell_limit()
    {
        var windows = Enumerable.Range(0, 3)
            .Select(i => UsageWindow.Create("a_very_long_window_key_number_" + i, 50, Now.AddDays(6)))
            .ToList();
        var status = new PollStatus(PollState.Ok, Snapshot() with { Windows = windows }, Now, Now, null, null, 0);

        var tooltip = TrayIconViewModel.BuildTooltip(status, Now);

        tooltip.Length.ShouldBeLessThanOrEqualTo(TrayIconViewModel.TooltipLimit);
        tooltip.ShouldEndWith("…");
    }
}
