using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Pricing;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class PricingTests
{
    private const string Json = """
        {"effectiveDate":"2026-06-24","currency":"USD","models":[
          {"id":"claude-opus-4","match":"prefix","input":15,"output":75,"cacheWrite5m":18.75,"cacheWrite1h":30,"cacheRead":1.5},
          {"id":"claude-opus-4-8","match":"prefix","input":5,"output":25},
          {"id":"exact-model","input":1,"output":2,"cacheWrite5m":3,"cacheWrite1h":4,"cacheRead":5}]}
        """;

    private static PricingTable Table() => PricingLoader.Parse(Json, "test");

    [Fact]
    public void Parses_metadata_and_fills_default_cache_prices()
    {
        var table = Table();

        table.EffectiveDate.ShouldBe(new DateOnly(2026, 6, 24));
        table.Currency.ShouldBe("USD");
        table.Models.Count.ShouldBe(3);
        var opus48 = table.Find("claude-opus-4-8").ShouldNotBeNull();
        opus48.CacheWrite5m.ShouldBe(6.25m);
        opus48.CacheWrite1h.ShouldBe(10m);
        opus48.CacheRead.ShouldBe(0.5m);
    }

    [Fact]
    public void The_longest_prefix_wins_and_exact_beats_prefix()
    {
        var table = Table();

        table.Find("claude-opus-4-8-20260101").ShouldNotBeNull().Id.ShouldBe("claude-opus-4-8");
        table.Find("claude-opus-4-20250514").ShouldNotBeNull().Id.ShouldBe("claude-opus-4");
        table.Find("exact-model").ShouldNotBeNull().Input.ShouldBe(1m);
        table.Find("exact-model-2").ShouldBeNull();
        table.Find("claude-mystery").ShouldBeNull();
        table.Find("").ShouldBeNull();
    }

    [Fact]
    public void Cost_is_per_million_tokens_across_all_five_counters()
    {
        var tokens = new TokenTotals(1_000_000, 100_000, 10_000, 1_000, 200_000, 3);

        Table().Cost("exact-model", tokens).ShouldBe(1m + 0.2m + 0.03m + 0.004m + 1m);
        Table().Cost("unknown", tokens).ShouldBeNull();
    }

    [Fact]
    public void Loader_prefers_the_override_and_falls_back_to_the_bundled_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClaudeTrayApp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var bundled = Path.Combine(directory, "pricing.json");
        File.WriteAllText(bundled, Json);
        var broken = Path.Combine(directory, "broken.json");
        File.WriteAllText(broken, "{ not json");
        var log = new ListLogger<PricingTests>();

        PricingLoader.Load(broken, bundled, log).SourcePath.ShouldBe(bundled);
        PricingLoader.Load(Path.Combine(directory, "missing.json"), bundled, log).Models.Count.ShouldBe(3);
        PricingLoader.Load(null, Path.Combine(directory, "missing.json"), log).IsEmpty.ShouldBeTrue();
        log.All.ShouldContain("could not be used");

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void The_bundled_pricing_file_parses_and_covers_current_models()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ClaudeTrayApp", "pricing.json");

        var table = PricingLoader.Parse(File.ReadAllText(Path.GetFullPath(path)), path);

        table.EffectiveDate.ShouldNotBeNull();
        table.Find("claude-fable-5-1").ShouldNotBeNull().CacheRead.ShouldBe(0.25m);
        table.Find("claude-opus-5").ShouldNotBeNull().Output.ShouldBe(25m);
        table.Find("claude-sonnet-4-5-20250929").ShouldNotBeNull().Input.ShouldBe(3m);
        table.Find("claude-haiku-4-5-20251001").ShouldNotBeNull().Input.ShouldBe(1m);
        table.Find("<synthetic>").ShouldBeNull();
    }
}
