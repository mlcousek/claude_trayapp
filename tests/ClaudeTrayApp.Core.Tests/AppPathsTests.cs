using ClaudeTrayApp.Core;
using ClaudeTrayApp.Core.Account;
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
    public void Exposes_the_override_and_the_desktop_cli_folder_for_the_refresh_nudge()
    {
        var plain = new AppPaths(Local, Roaming, Home);
        var overridden = new AppPaths(Local, Roaming, Home, Path.Combine("elsewhere", "claude"));

        plain.ClaudeConfigDirOverride.ShouldBeNull();
        overridden.ClaudeConfigDirOverride.ShouldBe(Path.Combine("elsewhere", "claude"));
        plain.ClaudeDesktopCliDirectory.ShouldBe(Path.Combine(Roaming, "Claude", "claude-code"));
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

    [Fact]
    public void FromEnvironment_prefers_rooted_profile_variables_over_the_known_folders()
    {
        var local = Path.GetFullPath(Path.Combine("fresh", "local"));
        var roaming = Path.GetFullPath(Path.Combine("fresh", "roaming"));
        var profile = Path.GetFullPath(Path.Combine("fresh", "profile"));
        var variables = new Dictionary<string, string?>
        {
            ["LOCALAPPDATA"] = local,
            ["APPDATA"] = roaming,
            ["USERPROFILE"] = profile,
        };

        var paths = AppPaths.FromEnvironment(name => variables.GetValueOrDefault(name), _ => throw new InvalidOperationException("known folder consulted"));

        paths.LocalRoot.ShouldBe(Path.Combine(local, "ClaudeTrayApp"));
        paths.RoamingRoot.ShouldBe(Path.Combine(roaming, "ClaudeTrayApp"));
        paths.ClaudeHome.ShouldBe(Path.Combine(profile, ".claude"));
        paths.ClaudeConfigFile.ShouldBe(Path.Combine(profile, ".claude.json"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    public void FromEnvironment_falls_back_to_the_known_folders_when_a_variable_is_unusable(string? value)
    {
        var known = new Dictionary<Environment.SpecialFolder, string>
        {
            [Environment.SpecialFolder.LocalApplicationData] = Path.GetFullPath(Path.Combine("known", "local")),
            [Environment.SpecialFolder.ApplicationData] = Path.GetFullPath(Path.Combine("known", "roaming")),
            [Environment.SpecialFolder.UserProfile] = Path.GetFullPath(Path.Combine("known", "profile")),
        };

        var paths = AppPaths.FromEnvironment(name => name == AppPaths.ClaudeConfigDirVariable ? null : value, folder => known[folder]);

        paths.LocalRoot.ShouldBe(Path.Combine(known[Environment.SpecialFolder.LocalApplicationData], "ClaudeTrayApp"));
        paths.RoamingRoot.ShouldBe(Path.Combine(known[Environment.SpecialFolder.ApplicationData], "ClaudeTrayApp"));
        paths.ClaudeHome.ShouldBe(Path.Combine(known[Environment.SpecialFolder.UserProfile], ".claude"));
    }

    [Fact]
    public void The_default_account_keeps_every_existing_path()
    {
        var paths = new AppPaths(Local, Roaming, Home, account: AccountContext.Default);

        paths.Account.IsDefault.ShouldBeTrue();
        paths.LocalRoot.ShouldBe(Path.Combine(Local, "ClaudeTrayApp"));
        paths.SharedLocalRoot.ShouldBe(paths.LocalRoot);
        paths.SettingsFile.ShouldBe(Path.Combine(Roaming, "ClaudeTrayApp", "settings.json"));
        paths.GlobalSettingsFile.ShouldBe(paths.SettingsFile);
        paths.UpdateStateFile.ShouldBe(Path.Combine(Local, "ClaudeTrayApp", "update-check.json"));
        paths.DefaultClaudeHome.ShouldBe(Path.Combine(Home, ".claude"));
    }

    [Fact]
    public void Another_account_reads_its_home_and_keeps_its_data_apart()
    {
        var home = Path.GetFullPath(Path.Combine("root", "home", ".claude-work"));
        var account = Account(home);

        var paths = new AppPaths(Local, Roaming, Home, account: account);

        var local = Path.Combine(Local, "ClaudeTrayApp", "accounts", account.Key!);
        var roaming = Path.Combine(Roaming, "ClaudeTrayApp", "accounts", account.Key!);
        paths.LocalRoot.ShouldBe(local);
        paths.RoamingRoot.ShouldBe(roaming);
        paths.CacheFile.ShouldBe(Path.Combine(local, "cache.json"));
        paths.DatabaseFile.ShouldBe(Path.Combine(local, "history.db"));
        paths.LogsDirectory.ShouldBe(Path.Combine(local, "logs"));
        paths.SettingsFile.ShouldBe(Path.Combine(roaming, "settings.json"));
        paths.GlobalSettingsFile.ShouldBe(Path.Combine(Roaming, "ClaudeTrayApp", "settings.json"));
        paths.UpdateStateFile.ShouldBe(Path.Combine(Local, "ClaudeTrayApp", "update-check.json"));
        paths.ClaudeHome.ShouldBe(home);
        paths.ClaudeCredentialsFile.ShouldBe(Path.Combine(home, ".credentials.json"));
        paths.ClaudeConfigFile.ShouldBe(Path.Combine(home, ".claude.json"));
        paths.ClaudeProjectsDirectory.ShouldBe(Path.Combine(home, "projects"));
        paths.ClaudeConfigDirOverride.ShouldBe(home);
    }

    [Fact]
    public void Another_account_wins_over_the_claude_config_dir_variable()
    {
        var home = Path.GetFullPath(Path.Combine("root", "home", ".claude-work"));

        var paths = new AppPaths(Local, Roaming, Home, Path.Combine("elsewhere", "claude"), Account(home));

        paths.ClaudeHome.ShouldBe(home);
        paths.ClaudeConfigDirOverride.ShouldBe(home);
    }

    [Fact]
    public void The_profile_home_chosen_explicitly_ignores_the_variable_and_keeps_its_config_in_the_profile()
    {
        var home = Path.GetFullPath(Path.Combine("root", "home"));
        var profileHome = Path.Combine(home, ".claude");
        var workHome = Path.Combine(home, ".claude-work");
        AccountContext.TryParse(["--claude-dir", profileHome], workHome, out var account, out var error).ShouldBeTrue(error);

        var paths = new AppPaths(Local, Roaming, home, workHome, account);

        account.IsDefault.ShouldBeFalse();
        paths.ClaudeHome.ShouldBe(profileHome);
        paths.ClaudeCredentialsFile.ShouldBe(Path.Combine(profileHome, ".credentials.json"));
        paths.ClaudeConfigFile.ShouldBe(Path.Combine(home, ".claude.json"));
        paths.ClaudeConfigDirOverride.ShouldBeNull();
        paths.LocalRoot.ShouldBe(Path.Combine(Local, "ClaudeTrayApp", "accounts", account.Key!));
    }

    [Fact]
    public void No_writable_file_of_another_account_lies_inside_its_claude_home()
    {
        var home = Path.GetFullPath(Path.Combine("root", "home", ".claude-work"));
        var paths = new AppPaths(Local, Roaming, Home, account: Account(home));

        string[] writable = [paths.CacheFile, paths.DatabaseFile, paths.LogsDirectory, paths.SettingsFile, paths.GlobalSettingsFile, paths.UpdateStateFile];

        writable.ShouldAllBe(file => !Path.GetFullPath(file).StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForAccount_keeps_the_roots_and_the_variable_for_the_default_account()
    {
        var home = Path.GetFullPath(Path.Combine("root", "home", ".claude-work"));
        var custom = Path.Combine("elsewhere", "claude");
        var defaults = new AppPaths(Local, Roaming, Home, custom);

        var work = defaults.ForAccount(Account(home));
        var back = work.ForAccount(AccountContext.Default);

        work.SharedLocalRoot.ShouldBe(defaults.LocalRoot);
        work.ClaudeHome.ShouldBe(home);
        back.ClaudeHome.ShouldBe(custom);
        back.LocalRoot.ShouldBe(defaults.LocalRoot);
    }

    [Fact]
    public void FromEnvironment_still_honours_the_claude_config_dir_variable()
    {
        var custom = Path.GetFullPath(Path.Combine("elsewhere", "claude-home"));

        var paths = AppPaths.FromEnvironment(
            name => name == AppPaths.ClaudeConfigDirVariable ? custom : null,
            _ => Path.GetFullPath("known"));

        paths.ClaudeHome.ShouldBe(custom);
        paths.ClaudeCredentialsFile.ShouldBe(Path.Combine(custom, ".credentials.json"));
        paths.ClaudeConfigFile.ShouldBe(Path.Combine(custom, ".claude.json"));
    }

    private static AccountContext Account(string home)
    {
        AccountContext.TryParse(["--claude-dir", home], Path.GetFullPath(Path.Combine(Home, ".claude")), out var account, out var error).ShouldBeTrue(error);
        return account;
    }
}
