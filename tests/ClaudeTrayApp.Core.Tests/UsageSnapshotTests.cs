using ClaudeTrayApp.Core.Domain;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class UsageSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(params string[] keys) =>
        UsageSnapshot.Empty(Now) with { Windows = keys.Select(k => UsageWindow.Create(k, 10, null)).ToList() };

    [Theory]
    [InlineData(null, "five_hour")]
    [InlineData("", "five_hour")]
    [InlineData("auto", "five_hour")]
    [InlineData("AUTO", "five_hour")]
    [InlineData("seven_day", "seven_day")]
    [InlineData("Seven_Day", "seven_day")]
    [InlineData("missing_window", "five_hour")]
    public void Window_for_a_setting_falls_back_to_the_primary_window(string? preferred, string expectedKey) =>
        Snapshot("seven_day", "five_hour", "nimbus_quill").WindowFor(preferred).ShouldNotBeNull().Key.ShouldBe(expectedKey);

    [Fact]
    public void Inactive_codename_windows_are_hidden_unless_asked()
    {
        var snapshot = UsageSnapshot.Empty(Now) with
        {
            Windows =
            [
                UsageWindow.Create("five_hour", 0, null),
                UsageWindow.Create("seven_day_opus", 0, null),
                UsageWindow.Create("nimbus_quill", 0, null),
                UsageWindow.Create("tangelo", 3, null),
                UsageWindow.Create("amber_ladder", 0, Now.AddHours(1)),
                UsageWindow.Create("copper_kite", 0, null, lockedReason: "limit_reached"),
            ],
        };

        snapshot.Windows[2].IsInactive.ShouldBeTrue();
        snapshot.Windows[0].IsInactive.ShouldBeTrue();
        snapshot.Windows[0].IsKnownKey.ShouldBeTrue();
        snapshot.Windows[2].IsKnownKey.ShouldBeFalse();
        snapshot.VisibleWindows(false).Select(w => w.Key).ShouldBe(["five_hour", "seven_day_opus", "tangelo", "amber_ladder", "copper_kite"]);
        snapshot.VisibleWindows(true).Count.ShouldBe(6);
    }

    [Fact]
    public void Without_a_five_hour_window_the_first_window_is_primary()
    {
        Snapshot("seven_day", "nimbus_quill").WindowFor("auto").ShouldNotBeNull().Key.ShouldBe("seven_day");
        Snapshot().WindowFor("auto").ShouldBeNull();
    }
}
