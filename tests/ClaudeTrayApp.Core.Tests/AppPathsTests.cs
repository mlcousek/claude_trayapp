using ClaudeTrayApp.Core;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class AppPathsTests
{
    private static readonly string Local = Path.Combine("root", "local");
    private static readonly string Roaming = Path.Combine("root", "roaming");
    private static readonly string Home = Path.Combine("root", "home");

    [Fact]
    public void Derives_every_path_from_the_given_roots()
    {
        var paths = new AppPaths(Local, Roaming, Home);

        paths.LocalRoot.ShouldBe(Path.Combine(Local, "ClaudeTrayApp"));
        paths.RoamingRoot.ShouldBe(Path.Combine(Roaming, "ClaudeTrayApp"));
        paths.LogsDirectory.ShouldBe(Path.Combine(Local, "ClaudeTrayApp", "logs"));
        paths.CacheFile.ShouldBe(Path.Combine(Local, "ClaudeTrayApp", "cache.json"));
        paths.DatabaseFile.ShouldBe(Path.Combine(Local, "ClaudeTrayApp", "history.db"));
        paths.SettingsFile.ShouldBe(Path.Combine(Roaming, "ClaudeTrayApp", "settings.json"));
        paths.ClaudeHome.ShouldBe(Path.Combine(Home, ".claude"));
        paths.ClaudeCredentialsFile.ShouldBe(Path.Combine(Home, ".claude", ".credentials.json"));
        paths.ClaudeProjectsDirectory.ShouldBe(Path.Combine(Home, ".claude", "projects"));
    }

    [Fact]
    public void Honours_the_claude_config_dir_override()
    {
        var custom = Path.Combine("elsewhere", "claude-home");

        var paths = new AppPaths(Local, Roaming, Home, custom);

        paths.ClaudeHome.ShouldBe(custom);
        paths.ClaudeCredentialsFile.ShouldBe(Path.Combine(custom, ".credentials.json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_the_profile_when_the_override_is_blank(string? overrideValue)
    {
        var paths = new AppPaths(Local, Roaming, Home, overrideValue);

        paths.ClaudeHome.ShouldBe(Path.Combine(Home, ".claude"));
    }

    [Fact]
    public void Rejects_blank_roots()
    {
        Should.Throw<ArgumentException>(() => new AppPaths("", Roaming, Home));
        Should.Throw<ArgumentException>(() => new AppPaths(Local, " ", Home));
        Should.Throw<ArgumentException>(() => new AppPaths(Local, Roaming, ""));
    }

    [Fact]
    public void FromEnvironment_returns_rooted_paths()
    {
        var paths = AppPaths.FromEnvironment();

        Path.IsPathRooted(paths.LocalRoot).ShouldBeTrue();
        Path.IsPathRooted(paths.RoamingRoot).ShouldBeTrue();
        Path.IsPathRooted(paths.ClaudeHome).ShouldBeTrue();
    }
}
