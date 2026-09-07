using System.Text.Json;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Providers;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class UsageResponseParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Parse(string json, string? planFallback = null)
    {
        using var document = JsonDocument.Parse(json);
        return UsageResponseParser.Parse(document.RootElement, Now, planFallback);
    }

    private static string Typical() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "usage", "typical.json"));

    [Fact]
    public void Parses_the_known_shape_into_ordered_windows()
    {
        var snapshot = Parse(Typical());

        snapshot.Windows.Select(w => w.Key).ShouldBe(["five_hour", "seven_day", "seven_day_sonnet", "nimbus_quill"]);
        snapshot.Windows[0].UtilizationPercent.ShouldBe(51);
        snapshot.Windows[0].ResetsAt.ShouldBe(new DateTimeOffset(2026, 9, 7, 16, 30, 0, TimeSpan.Zero));
        snapshot.Windows[1].UtilizationPercent.ShouldBe(10.5);
        snapshot.Windows[2].Status.ShouldBe(UsageWindowStatus.Critical);
        snapshot.Windows[3].ResetsAt.ShouldBeNull();
        snapshot.Source.ShouldBe(UsageSource.Live);
        snapshot.LastUpdated.ShouldBe(Now);
    }

    [Fact]
    public void Null_windows_and_non_window_blocks_are_ignored()
    {
        var snapshot = Parse(Typical());

        snapshot.Windows.ShouldNotContain(w => w.Key == "seven_day_opus" || w.Key == "seven_day_oauth_apps");
        snapshot.Windows.ShouldNotContain(w => w.Key == "spend" || w.Key == "limits" || w.Key == "extra_usage");
    }

    [Fact]
    public void Parses_extra_usage()
    {
        var overage = Parse(Typical()).Overage.ShouldNotBeNull();

        overage.IsEnabled.ShouldBeTrue();
        overage.UsedAmount.ShouldBe(12.5m);
        overage.LimitAmount.ShouldBe(50m);
        overage.UtilizationPercent.ShouldBe(25);
        overage.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Derives_overage_utilization_when_the_endpoint_omits_it()
    {
        var overage = Parse("""{"extra_usage":{"is_enabled":true,"monthly_limit":40,"used_credits":10}}""").Overage.ShouldNotBeNull();

        overage.UtilizationPercent.ShouldBe(25);
        overage.LimitAmount.ShouldBe(40m);
    }

    [Fact]
    public void Scales_extra_usage_amounts_by_decimal_places()
    {
        var overage = Parse("""{"extra_usage":{"is_enabled":true,"monthly_limit":10000,"used_credits":250,"decimal_places":2,"currency":"USD"}}""").Overage.ShouldNotBeNull();

        overage.LimitAmount.ShouldBe(100m);
        overage.UsedAmount.ShouldBe(2.5m);
        overage.UtilizationPercent.ShouldBe(2.5);
    }

    [Fact]
    public void Falls_back_to_the_spend_block_for_money()
    {
        var json = """
            {"extra_usage":{"is_enabled":true,"monthly_limit":null,"used_credits":null,"utilization":null,"currency":null,"decimal_places":2},
             "spend":{"used":{"amount_minor":250,"currency":"USD","exponent":2},"limit":{"amount_minor":10000,"currency":"USD","exponent":2},"percent":2.5,"enabled":true}}
            """;

        var overage = Parse(json).Overage.ShouldNotBeNull();

        overage.IsEnabled.ShouldBeTrue();
        overage.UsedAmount.ShouldBe(2.5m);
        overage.LimitAmount.ShouldBe(100m);
        overage.UtilizationPercent.ShouldBe(2.5);
        overage.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Spend_alone_is_enough_for_overage()
    {
        var overage = Parse("""{"spend":{"used":{"amount_minor":0,"currency":"EUR","exponent":2},"limit":null,"percent":0,"enabled":false}}""").Overage.ShouldNotBeNull();

        overage.IsEnabled.ShouldBeFalse();
        overage.UsedAmount.ShouldBe(0m);
        overage.LimitAmount.ShouldBeNull();
        overage.Currency.ShouldBe("EUR");
    }

    [Fact]
    public void Reads_a_locked_reason()
    {
        var window = Parse("""{"five_hour":{"utilization":100,"resets_at":null,"locked_reason":"limit_reached"}}""").Windows.ShouldHaveSingleItem();

        window.IsLocked.ShouldBeTrue();
        window.LockedReason.ShouldBe("limit_reached");
        Parse(Typical()).Windows[0].IsLocked.ShouldBeFalse();
    }

    [Fact]
    public void Unknown_keys_still_render_with_a_humanised_name()
    {
        var snapshot = Parse("""{"seven_day_cowork":{"utilization":12,"resets_at":"2026-09-10T00:00:00Z"}}""");

        var window = snapshot.Windows.ShouldHaveSingleItem();
        window.Key.ShouldBe("seven_day_cowork");
        window.DisplayName.ShouldBe("7-day cowork");
    }

    [Fact]
    public void Known_windows_sort_first_and_unknown_ones_alphabetically_after()
    {
        var snapshot = Parse("""{"zeta":{"utilization":1},"seven_day":{"utilization":2},"alpha":{"utilization":3},"five_hour":{"utilization":4}}""");

        snapshot.Windows.Select(w => w.Key).ShouldBe(["five_hour", "seven_day", "alpha", "zeta"]);
    }

    [Theory]
    [InlineData("150", 100, UsageWindowStatus.Exhausted)]
    [InlineData("-5", 0, UsageWindowStatus.Ok)]
    [InlineData("\"42.5\"", 42.5, UsageWindowStatus.Ok)]
    [InlineData("70", 70, UsageWindowStatus.Warning)]
    [InlineData("89.9", 89.9, UsageWindowStatus.Warning)]
    [InlineData("90", 90, UsageWindowStatus.Critical)]
    public void Maps_percentages_and_statuses(string raw, double expected, UsageWindowStatus status)
    {
        var window = Parse("{\"five_hour\":{\"utilization\":" + raw + "}}").Windows.ShouldHaveSingleItem();

        window.UtilizationPercent.ShouldBe(expected);
        window.Status.ShouldBe(status);
    }

    [Fact]
    public void Reads_epoch_reset_times()
    {
        var window = Parse("""{"five_hour":{"utilization":1,"resets_at":1788798600}}""").Windows.ShouldHaveSingleItem();

        window.ResetsAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1788798600));
    }

    [Fact]
    public void Uses_the_plan_from_the_body_before_the_fallback()
    {
        Parse("""{"plan":"team","five_hour":{"utilization":1}}""", "max").PlanTier.ShouldBe("team");
        Parse("""{"five_hour":{"utilization":1}}""", "max").PlanTier.ShouldBe("max");
        Parse("""{"five_hour":{"utilization":1}}""").PlanTier.ShouldBeNull();
    }

    [Fact]
    public void An_empty_object_yields_no_windows_rather_than_an_error()
    {
        var snapshot = Parse("{}");

        snapshot.Windows.ShouldBeEmpty();
        snapshot.Overage.ShouldBeNull();
        snapshot.PrimaryWindow.ShouldBeNull();
    }

    [Fact]
    public void A_non_object_root_is_a_parse_error() =>
        Should.Throw<JsonException>(() => Parse("[1,2,3]"));

    [Fact]
    public void Primary_window_prefers_five_hour()
    {
        Parse(Typical()).PrimaryWindow.ShouldNotBeNull().Key.ShouldBe("five_hour");
        Parse("""{"seven_day":{"utilization":2}}""").PrimaryWindow.ShouldNotBeNull().Key.ShouldBe("seven_day");
    }
}
