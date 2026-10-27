using ClaudeTrayApp.Core.Account;
using ClaudeTrayApp.Startup;
using Shouldly;

namespace ClaudeTrayApp.Tests;

public class AutostartManagerTests
{
    private const string Exe = @"C:\Tools\Claude Usage Tray\ClaudeTrayApp.exe";

    [Fact]
    public void The_run_command_quotes_the_executable() =>
        AutostartManager.BuildCommand(Exe).ShouldBe("\"" + Exe + "\"");

    [Theory]
    [InlineData("\"C:\\Tools\\Claude Usage Tray\\ClaudeTrayApp.exe\"", true)]
    [InlineData("\"c:\\tools\\claude usage tray\\claudetrayapp.exe\" --minimized", true)]
    [InlineData("  \"C:\\Tools\\Claude Usage Tray\\ClaudeTrayApp.exe\"  ", true)]
    [InlineData("\"C:\\Other\\ClaudeTrayApp.exe\"", false)]
    [InlineData("C:\\Other\\ClaudeTrayApp.exe --flag", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_stored_value_matches_only_this_executable(string? stored, bool expected) =>
        AutostartManager.Matches(stored, Exe).ShouldBe(expected);

    [Fact]
    public void The_default_account_keeps_its_name_command_and_marker()
    {
        AutostartManager.BuildCommand(Exe, AccountContext.Default).ShouldBe("\"" + Exe + "\"");
        AutostartManager.RunValueNameFor(AccountContext.Default).ShouldBe("ClaudeUsageTray");
        AutostartManager.MarkerKeyPathFor(AccountContext.Default).ShouldBe(@"Software\ClaudeUsageTray");
    }

    [Fact]
    public void Another_account_has_its_own_name_marker_and_arguments()
    {
        var account = Account(@"C:\Users\me\.claude-work", "--label", "Work");

        AutostartManager.RunValueNameFor(account).ShouldBe("ClaudeUsageTray." + account.Key);
        AutostartManager.MarkerKeyPathFor(account).ShouldBe(@"Software\ClaudeUsageTray\Accounts\" + account.Key);
        AutostartManager.BuildCommand(Exe, account)
            .ShouldBe("\"" + Exe + "\" --claude-dir \"C:\\Users\\me\\.claude-work\" --label \"Work\"");
    }

    [Fact]
    public void The_label_is_stored_only_when_it_was_given()
    {
        var account = Account(@"C:\Users\me\.claude-work");

        AutostartManager.BuildCommand(Exe, account).ShouldBe("\"" + Exe + "\" --claude-dir \"C:\\Users\\me\\.claude-work\"");
    }

    [Fact]
    public void A_command_with_arguments_still_matches_the_executable()
    {
        var command = AutostartManager.BuildCommand(Exe, Account(@"C:\Users\me\.claude-work"));

        AutostartManager.Matches(command, Exe).ShouldBeTrue();
    }

    [Theory]
    [InlineData(@"C:\Users\me\.claude-work", "\"C:\\Users\\me\\.claude-work\"")]
    [InlineData(@"D:\", "\"D:\\\\\"")]
    [InlineData("Work and play", "\"Work and play\"")]
    public void Arguments_are_quoted_so_a_trailing_backslash_cannot_escape_the_quote(string value, string expected) =>
        AutostartManager.QuoteArgument(value).ShouldBe(expected);

    [Fact]
    public void An_unquoted_path_without_spaces_matches()
    {
        const string Plain = @"C:\Tools\ClaudeTrayApp.exe";

        AutostartManager.Matches(Plain + " --something", Plain).ShouldBeTrue();
        AutostartManager.Matches(Plain, Plain).ShouldBeTrue();
    }

    private static AccountContext Account(string home, params string[] extra)
    {
        AccountContext.TryParse(["--claude-dir", home, .. extra], @"C:\Users\me\.claude", out var account, out var error).ShouldBeTrue(error);
        return account;
    }
}
