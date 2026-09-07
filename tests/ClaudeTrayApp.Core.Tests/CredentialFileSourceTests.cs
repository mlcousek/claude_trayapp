using System.Text;
using ClaudeTrayApp.Core.Credentials;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class CredentialFileSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "credentials", name);

    private static Task<CredentialLookup> ReadAsync(string fixture) =>
        new CredentialFileSource(Fixture(fixture)).ReadAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reads_the_claude_ai_oauth_section()
    {
        var lookup = await ReadAsync("claude-ai-oauth.json");

        lookup.Status.ShouldBe(CredentialStatus.Found);
        var credentials = lookup.Credentials.ShouldNotBeNull();
        credentials.AccessToken.ShouldBe("FAKE-ACCESS-TOKEN-FOR-TESTS-0123456789");
        credentials.ExpiresAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(4102444800000));
        credentials.SubscriptionType.ShouldBe("max");
        credentials.RateLimitTier.ShouldBe("default_claude_max_5x");
        credentials.IsExpired(Now).ShouldBeFalse();
    }

    [Fact]
    public async Task Never_uses_tokens_from_the_mcp_section()
    {
        var lookup = await ReadAsync("mcp-only.json");

        lookup.Status.ShouldBe(CredentialStatus.Invalid);
        lookup.Credentials.ShouldBeNull();
        lookup.Detail.ShouldNotBeNull().ShouldContain("no Claude access token");
    }

    [Fact]
    public async Task Accepts_a_flat_layout_with_a_seconds_expiry()
    {
        var lookup = await ReadAsync("flat-layout.json");

        lookup.Status.ShouldBe(CredentialStatus.Found);
        var credentials = lookup.Credentials.ShouldNotBeNull();
        credentials.AccessToken.ShouldBe("FAKE-FLAT-TOKEN-FOR-TESTS-0123456789");
        credentials.ExpiresAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(4102444800));
        credentials.SubscriptionType.ShouldBe("pro");
    }

    [Fact]
    public async Task Reports_a_missing_file_without_throwing()
    {
        var lookup = await ReadAsync("does-not-exist.json");

        lookup.Status.ShouldBe(CredentialStatus.NotFound);
        lookup.Detail.ShouldNotBeNull().ShouldContain("Sign in with Claude Code");
    }

    [Fact]
    public async Task Reports_malformed_json_as_invalid()
    {
        var lookup = await ReadAsync("malformed.json");

        lookup.Status.ShouldBe(CredentialStatus.Invalid);
        lookup.Credentials.ShouldBeNull();
    }

    [Fact]
    public async Task Reports_a_missing_token_as_invalid()
    {
        var lookup = await ReadAsync("no-token.json");

        lookup.Status.ShouldBe(CredentialStatus.Invalid);
    }

    [Fact]
    public async Task Flags_an_expired_token()
    {
        var lookup = await ReadAsync("expired.json");

        lookup.Status.ShouldBe(CredentialStatus.Found);
        lookup.Credentials.ShouldNotBeNull().IsExpired(Now).ShouldBeTrue();
    }

    [Fact]
    public void Parses_an_iso_string_expiry()
    {
        var json = """{"claudeAiOauth":{"accessToken":"FAKE-ISO-TOKEN-0123456789","expiresAt":"2026-09-07T11:40:07Z"}}""";

        var lookup = CredentialFileSource.Parse(Encoding.UTF8.GetBytes(json));

        lookup.Status.ShouldBe(CredentialStatus.Found);
        lookup.Credentials.ShouldNotBeNull().ExpiresAt.ShouldBe(new DateTimeOffset(2026, 9, 7, 11, 40, 7, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":\"\"}}")]
    [InlineData("{\"claudeAiOauth\":{\"accessToken\":42}}")]
    public void Rejects_shapes_without_a_usable_token(string json)
    {
        var lookup = CredentialFileSource.Parse(Encoding.UTF8.GetBytes(json));

        lookup.Status.ShouldBe(CredentialStatus.Invalid);
        lookup.Credentials.ShouldBeNull();
    }

    [Fact]
    public async Task Credentials_to_string_never_contains_the_token()
    {
        var lookup = await ReadAsync("claude-ai-oauth.json");

        var text = lookup.Credentials.ShouldNotBeNull().ToString();

        text.ShouldNotContain("FAKE-ACCESS-TOKEN");
        text.ShouldContain("[redacted]");
    }

    [Fact]
    public void Description_names_the_file_but_no_secret()
    {
        var source = new CredentialFileSource(Fixture("claude-ai-oauth.json"));

        source.Description.ShouldContain("claude-ai-oauth.json");
        source.Description.ShouldNotContain("FAKE");
    }

    [Fact]
    public void Rejects_a_blank_path() =>
        Should.Throw<ArgumentException>(() => new CredentialFileSource(" "));
}
