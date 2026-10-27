using ClaudeTrayApp.Core.Account;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class AccountContextTests
{
    private static readonly string Profile = Path.GetFullPath(Path.Combine("profile"));
    private static readonly string DefaultHome = Path.Combine(Profile, ".claude");
    private static readonly string WorkHome = Path.Combine(Profile, ".claude-work");

    [Fact]
    public void No_arguments_is_the_default_account()
    {
        AccountContext.TryParse([], DefaultHome, out var account, out var error).ShouldBeTrue();

        error.ShouldBeNull();
        account.IsDefault.ShouldBeTrue();
        account.ClaudeHome.ShouldBeNull();
        account.Key.ShouldBeNull();
        account.LabelArgument.ShouldBeNull();
    }

    [Fact]
    public void Each_account_has_its_own_stable_tray_icon_id_and_the_default_keeps_the_libraries()
    {
        AccountContext.TryParse([], DefaultHome, out var defaultAccount, out _).ShouldBeTrue();
        AccountContext.TryParse(["--claude-dir", WorkHome], DefaultHome, out var work, out _).ShouldBeTrue();
        AccountContext.TryParse(["--claude-dir", WorkHome], DefaultHome, out var workAgain, out _).ShouldBeTrue();
        AccountContext.TryParse(["--claude-dir", Path.Combine(Profile, ".claude-other")], DefaultHome, out var other, out _).ShouldBeTrue();

        defaultAccount.TrayIconId.ShouldBeNull();
        work.TrayIconId.ShouldNotBeNull().ShouldNotBe(Guid.Empty);
        work.TrayIconId.ShouldBe(workAgain.TrayIconId);
        work.TrayIconId.ShouldNotBe(other.TrayIconId);
    }

    [Fact]
    public void Unrelated_arguments_are_left_alone()
    {
        AccountContext.TryParse(["--probe", "--capture-flyout", "out.png"], DefaultHome, out var account, out _).ShouldBeTrue();

        account.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void Claude_dir_selects_another_account()
    {
        AccountContext.TryParse(["--claude-dir", WorkHome, "--label", "Work"], DefaultHome, out var account, out var error).ShouldBeTrue();

        error.ShouldBeNull();
        account.IsDefault.ShouldBeFalse();
        account.ClaudeHome.ShouldBe(WorkHome);
        account.Key.ShouldNotBeNull().ShouldStartWith("claude-work-");
        account.LabelArgument.ShouldBe("Work");
    }

    [Fact]
    public void Switches_are_case_insensitive()
    {
        AccountContext.TryParse(["--CLAUDE-DIR", WorkHome], DefaultHome, out var account, out _).ShouldBeTrue();

        account.ClaudeHome.ShouldBe(WorkHome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\\")]
    public void The_default_home_in_any_spelling_is_the_default_account(string suffix)
    {
        AccountContext.TryParse(["--claude-dir", DefaultHome.ToUpperInvariant() + suffix, "--label", "Ignored"], DefaultHome, out var account, out _).ShouldBeTrue();

        account.IsDefault.ShouldBeTrue();
        account.LabelArgument.ShouldBeNull();
    }

    [Fact]
    public void An_explicit_home_wins_over_the_environment_variable()
    {
        // CLAUDE_CONFIG_DIR points the default account at the work home; --claude-dir asks for the profile home.
        AccountContext.TryParse(["--claude-dir", DefaultHome], WorkHome, out var personal, out _).ShouldBeTrue();
        AccountContext.TryParse(["--claude-dir", WorkHome], WorkHome, out var work, out _).ShouldBeTrue();

        personal.IsDefault.ShouldBeFalse();
        personal.ClaudeHome.ShouldBe(DefaultHome);
        work.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void A_trailing_separator_is_trimmed()
    {
        AccountContext.TryParse(["--claude-dir", WorkHome + "\\"], DefaultHome, out var account, out _).ShouldBeTrue();

        account.ClaudeHome.ShouldBe(WorkHome);
    }

    [Fact]
    public void A_relative_path_is_resolved_to_an_absolute_one()
    {
        AccountContext.TryParse(["--claude-dir", ".claude-relative"], DefaultHome, out var account, out _).ShouldBeTrue();

        account.ClaudeHome.ShouldBe(Path.GetFullPath(".claude-relative"));
        Path.IsPathRooted(account.ClaudeHome).ShouldBeTrue();
    }

    [Theory]
    [InlineData(new[] { "--claude-dir" }, "needs a folder path")]
    [InlineData(new[] { "--claude-dir", "--label", "Work" }, "needs a folder path")]
    [InlineData(new[] { "--claude-dir", "  " }, "needs a folder path")]
    [InlineData(new[] { "--claude-dir", "a", "--claude-dir", "b" }, "more than once")]
    [InlineData(new[] { "--claude-dir", "a|b" }, "not a usable folder path")]
    [InlineData(new[] { "--claude-dir", "a", "--label" }, "needs a name")]
    [InlineData(new[] { "--claude-dir", "a", "--label", "x", "--label", "y" }, "more than once")]
    public void Unusable_arguments_are_refused_with_a_reason(string[] args, string reason)
    {
        AccountContext.TryParse(args, DefaultHome, out var account, out var error).ShouldBeFalse();

        error.ShouldNotBeNull().ShouldContain(reason);
        account.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void The_key_is_stable_for_one_path_whatever_its_spelling()
    {
        AccountContext.KeyFor(WorkHome).ShouldBe(AccountContext.KeyFor(WorkHome));
        AccountContext.KeyFor(WorkHome).ShouldBe(AccountContext.KeyFor(WorkHome.ToUpperInvariant()));
    }

    [Fact]
    public void Folders_with_the_same_name_get_different_keys()
    {
        var first = AccountContext.KeyFor(Path.GetFullPath(Path.Combine("a", ".claude-work")));
        var second = AccountContext.KeyFor(Path.GetFullPath(Path.Combine("b", ".claude-work")));

        first.ShouldNotBe(second);
        first.ShouldStartWith("claude-work-");
        second.ShouldStartWith("claude-work-");
    }

    [Theory]
    [InlineData(".claude-work", "claude-work-")]
    [InlineData("Claude Work (ACME)", "claude-work-acme-")]
    [InlineData("._.", "account-")]
    [InlineData("a-very-long-folder-name-for-an-account-home", "a-very-long-folder-name-")]
    public void The_key_starts_with_a_readable_slug(string folder, string prefix)
    {
        var key = AccountContext.KeyFor(Path.GetFullPath(folder));

        key.ShouldStartWith(prefix);
        key.Length.ShouldBe(prefix.Length + 8);
        key.ShouldMatch("^[a-z0-9-]+$");
    }
}
