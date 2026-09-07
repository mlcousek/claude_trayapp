using System.Text;
using ClaudeTrayApp.Core.Analytics;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class JsonlLineParserTests
{
    private static string[] FixtureLines() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sessions", "sample.jsonl"));

    private static UsageEvent? Parse(string line) => JsonlLineParser.TryParse(Encoding.UTF8.GetBytes(line));

    [Fact]
    public void Parses_an_assistant_record_with_the_cache_split()
    {
        var usageEvent = Parse(FixtureLines()[1]).ShouldNotBeNull();

        usageEvent.MessageId.ShouldBe("msg_1");
        usageEvent.RequestId.ShouldBe("req_1");
        usageEvent.Timestamp.ShouldBe(new DateTimeOffset(2026, 9, 7, 5, 44, 13, TimeSpan.Zero));
        usageEvent.Model.ShouldBe("claude-fable-5-1");
        usageEvent.Project.ShouldBe("C:/work/alpha");
        usageEvent.SessionId.ShouldBe("s1");
        usageEvent.InputTokens.ShouldBe(2);
        usageEvent.OutputTokens.ShouldBe(564);
        usageEvent.CacheWrite5mTokens.ShouldBe(0);
        usageEvent.CacheWrite1hTokens.ShouldBe(32271);
        usageEvent.CacheReadTokens.ShouldBe(42113);
        usageEvent.TotalTokens.ShouldBe(2 + 564 + 32271 + 42113);
    }

    [Fact]
    public void Repeated_content_blocks_share_the_same_dedupe_key()
    {
        var first = Parse(FixtureLines()[1]).ShouldNotBeNull();
        var second = Parse(FixtureLines()[2]).ShouldNotBeNull();

        (first.MessageId, first.RequestId).ShouldBe((second.MessageId, second.RequestId));
    }

    [Fact]
    public void Legacy_cache_creation_without_a_split_counts_as_five_minute_writes()
    {
        var usageEvent = Parse(FixtureLines()[3]).ShouldNotBeNull();

        usageEvent.CacheWrite5mTokens.ShouldBe(500);
        usageEvent.CacheWrite1hTokens.ShouldBe(0);
        usageEvent.Model.ShouldBe("claude-sonnet-4-5-20250929");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void Non_usage_and_malformed_lines_yield_null(int index) =>
        Parse(FixtureLines()[index]).ShouldBeNull();

    [Fact]
    public void A_truncated_line_yields_null_instead_of_throwing()
    {
        var lines = FixtureLines();
        Parse(lines[^1]).ShouldBeNull();
        Parse("").ShouldBeNull();
        Parse("{\"type\":\"assistant\"").ShouldBeNull();
    }

    [Fact]
    public void Falls_back_to_the_line_uuid_when_the_message_has_no_id()
    {
        var usageEvent = Parse("""{"type":"assistant","uuid":"line-7","timestamp":"2026-09-07T06:00:00Z","message":{"role":"assistant","model":"m","usage":{"input_tokens":1,"output_tokens":1}}}""").ShouldNotBeNull();

        usageEvent.MessageId.ShouldBe("line-7");
        usageEvent.RequestId.ShouldBe(string.Empty);
    }
}
