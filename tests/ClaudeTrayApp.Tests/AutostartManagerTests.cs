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
    public void An_unquoted_path_without_spaces_matches()
    {
        const string Plain = @"C:\Tools\ClaudeTrayApp.exe";

        AutostartManager.Matches(Plain + " --something", Plain).ShouldBeTrue();
        AutostartManager.Matches(Plain, Plain).ShouldBeTrue();
    }
}
