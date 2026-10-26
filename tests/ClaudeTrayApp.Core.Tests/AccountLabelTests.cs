using System.Text;
using ClaudeTrayApp.Core.Account;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class AccountLabelTests
{
    private static readonly string DefaultHome = Path.GetFullPath(Path.Combine("profile", ".claude"));
    private static readonly string WorkHome = Path.GetFullPath(Path.Combine("profile", ".claude-work"));

    [Fact]
    public void The_default_account_has_no_label()
    {
        AccountLabel.Resolve(AccountContext.Default, Info("Acme Corp")).ShouldBeNull();
    }

    [Fact]
    public void The_label_argument_wins()
    {
        AccountLabel.Resolve(Work("--label", "Work"), Info("Acme Corp")).ShouldBe("Work");
    }

    [Fact]
    public void The_organisation_name_comes_next()
    {
        AccountLabel.Resolve(Work(), Info("Acme Corp")).ShouldBe("Acme Corp");
    }

    [Fact]
    public void An_organisation_named_after_an_email_address_is_skipped()
    {
        var label = AccountLabel.Resolve(Work(), Info("me@example.com's Organization"));

        label.ShouldBe("claude-work");
    }

    [Fact]
    public void The_folder_name_is_the_fallback()
    {
        AccountLabel.Resolve(Work(), Info(null)).ShouldBe("claude-work");
        AccountLabel.Resolve(Work(), null).ShouldBe("claude-work");
    }

    [Fact]
    public void Organisation_names_read_from_claude_json_are_used()
    {
        var info = AccountInfoFileSource.Parse(Encoding.UTF8.GetBytes("""{ "oauthAccount": { "emailAddress": "dev@acme.test", "organizationName": "Acme Corp" } }"""));

        AccountLabel.Resolve(Work(), info).ShouldBe("Acme Corp");
    }

    [Fact]
    public void An_overlong_label_is_cut_to_32_characters()
    {
        var label = AccountLabel.Resolve(Work("--label", new string('x', 50)), null);

        label.ShouldBe(new string('x', 32));
    }

    [Fact]
    public void Quotes_and_control_characters_are_removed()
    {
        AccountLabel.Sanitize("  \"Work\"\t\n ").ShouldBe("Work");
        AccountLabel.Sanitize("A\u0007B").ShouldBe("AB");
    }

    [Fact]
    public void A_label_with_nothing_left_falls_through()
    {
        AccountLabel.Sanitize("\"\"").ShouldBeNull();
        AccountLabel.Resolve(Work("--label", "\"\""), Info("  ")).ShouldBe("claude-work");
    }

    private static AccountContext Work(params string[] extra)
    {
        AccountContext.TryParse(["--claude-dir", WorkHome, .. extra], DefaultHome, out var account, out var error).ShouldBeTrue(error);
        return account;
    }

    private static AccountInfo Info(string? organization) => AccountInfo.Empty with { OrganizationName = organization };
}
