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
    public void Without_a_five_hour_window_the_first_window_is_primary()
    {
        Snapshot("seven_day", "nimbus_quill").WindowFor("auto").ShouldNotBeNull().Key.ShouldBe("seven_day");
        Snapshot().WindowFor("auto").ShouldBeNull();
    }
}
