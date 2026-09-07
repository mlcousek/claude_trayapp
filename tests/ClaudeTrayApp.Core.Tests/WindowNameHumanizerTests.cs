using ClaudeTrayApp.Core.Domain;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class WindowNameHumanizerTests
{
    [Theory]
    [InlineData("five_hour", "5-hour")]
    [InlineData("seven_day", "7-day")]
    [InlineData("seven_day_opus", "7-day Opus")]
    [InlineData("seven_day_sonnet", "7-day Sonnet")]
    [InlineData("seven_day_oauth_apps", "7-day OAuth apps")]
    [InlineData("seven_day_cowork", "7-day cowork")]
    [InlineData("nimbus_quill", "Nimbus quill")]
    [InlineData("30_minute", "30-minute")]
    [InlineData("monthly_api_budget", "Monthly API budget")]
    [InlineData("fiveHour", "5-hour")]
    [InlineData("SEVEN_DAY", "7-day")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Humanizes_keys(string? key, string expected) =>
        WindowNameHumanizer.Humanize(key).ShouldBe(expected);
}
