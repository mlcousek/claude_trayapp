using System.Text;
using ClaudeTrayApp.Core;
using ClaudeTrayApp.Core.Account;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class AccountInfoFileSourceTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "account", name);

    [Fact]
    public async Task Reads_the_oauth_account_block()
    {
        var info = await new AccountInfoFileSource(Fixture("claude-json.json")).ReadAsync(TestContext.Current.CancellationToken);

        info.Email.ShouldBe("someone@example.com");
        info.DisplayName.ShouldBe("Someone");
        info.OrganizationName.ShouldBe("Someone's Organization");
        info.BillingType.ShouldBe("stripe_subscription");
        info.OrganizationRateLimitTier.ShouldBe("default_claude_max_5x");
        info.HasExtraUsageEnabled.ShouldBe(true);
        info.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task A_missing_file_yields_empty() =>
        (await new AccountInfoFileSource(Fixture("nope.json")).ReadAsync(TestContext.Current.CancellationToken)).ShouldBe(AccountInfo.Empty);

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"numStartups\":1}")]
    [InlineData("{\"oauthAccount\":\"nope\"}")]
    public void Anything_without_an_account_block_yields_empty(string json) =>
        AccountInfoFileSource.Parse(Encoding.UTF8.GetBytes(json)).ShouldBe(AccountInfo.Empty);

    [Theory]
    [InlineData("someone@example.com", "s***@example.com")]
    [InlineData("a@b.co", "a***@b.co")]
    [InlineData("no-at-sign", "***")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Masks_emails_without_revealing_length(string? email, string expected) =>
        AccountInfo.MaskEmail(email).ShouldBe(expected);

    [Fact]
    public void To_string_never_contains_the_full_email()
    {
        var text = new AccountInfo("someone@example.com", "Someone", "Org", null, null, null).ToString();

        text.ShouldNotContain("someone@");
        text.ShouldContain("s***@example.com");
    }

    [Fact]
    public void Config_file_path_follows_the_claude_home_override()
    {
        new AppPaths("l", "r", Path.Combine("root", "home")).ClaudeConfigFile.ShouldBe(Path.Combine("root", "home", ".claude.json"));
        new AppPaths("l", "r", "home", Path.Combine("x", "cfg")).ClaudeConfigFile.ShouldBe(Path.Combine("x", "cfg", ".claude.json"));
    }
}
