using ClaudeTrayApp.Core.Account;

namespace ClaudeTrayApp.Core;

/// <summary>
/// Resolves every folder and file the app touches. Nothing here is tied to a particular user:
/// all roots come from the environment, and the Claude Code home honours the
/// <c>CLAUDE_CONFIG_DIR</c> override that Claude Code itself respects. An account started with
/// <c>--claude-dir</c> reads that home instead and keeps its own data under <c>accounts\&lt;key&gt;</c>.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Folder name used under both %LOCALAPPDATA% and %APPDATA%.</summary>
    public const string AppFolderName = "ClaudeTrayApp";

    /// <summary>Folder under both app roots that holds one subfolder per non-default account.</summary>
    public const string AccountsFolderName = "accounts";

    /// <summary>Environment variable Claude Code uses to relocate its home directory.</summary>
    public const string ClaudeConfigDirVariable = "CLAUDE_CONFIG_DIR";

    private readonly string _localAppData;
    private readonly string _roamingAppData;
    private readonly string _userProfile;
    private readonly string? _environmentOverride;

    public AppPaths(string localAppData, string roamingAppData, string userProfile, string? claudeConfigDirOverride = null, AccountContext? account = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);

        _localAppData = localAppData;
        _roamingAppData = roamingAppData;
        _userProfile = userProfile;
        _environmentOverride = string.IsNullOrWhiteSpace(claudeConfigDirOverride) ? null : claudeConfigDirOverride;
        Account = account ?? AccountContext.Default;

        SharedLocalRoot = Path.Combine(localAppData, AppFolderName);
        SharedRoamingRoot = Path.Combine(roamingAppData, AppFolderName);
        DefaultClaudeHome = Path.Combine(userProfile, ".claude");
        ClaudeDesktopCliDirectory = Path.Combine(roamingAppData, "Claude", "claude-code");

        if (Account.ClaudeHome is { } home && Account.Key is { } key)
        {
            LocalRoot = Path.Combine(SharedLocalRoot, AccountsFolderName, key);
            RoamingRoot = Path.Combine(SharedRoamingRoot, AccountsFolderName, key);
            ClaudeHome = home;
            ClaudeConfigFile = Path.Combine(home, ".claude.json");
            ClaudeConfigDirOverride = home;
        }
        else
        {
            LocalRoot = SharedLocalRoot;
            RoamingRoot = SharedRoamingRoot;
            ClaudeHome = _environmentOverride ?? DefaultClaudeHome;
            ClaudeConfigFile = Path.Combine(_environmentOverride ?? userProfile, ".claude.json");
            ClaudeConfigDirOverride = _environmentOverride;
        }
    }

    /// <summary>
    /// Builds the paths for the current user. <c>LOCALAPPDATA</c>, <c>APPDATA</c> and <c>USERPROFILE</c> win when the
    /// process has them set to rooted paths (so a run can be pointed at a fresh profile); otherwise the shell's known
    /// folders apply, which is what the variables normally hold anyway.
    /// </summary>
    public static AppPaths FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable, KnownFolder);

    /// <summary>Same as <see cref="FromEnvironment()"/> with the two lookups injected, for tests.</summary>
    public static AppPaths FromEnvironment(Func<string, string?> variable, Func<Environment.SpecialFolder, string> knownFolder)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(knownFolder);
        return new AppPaths(
            RootedOr(variable("LOCALAPPDATA"), () => knownFolder(Environment.SpecialFolder.LocalApplicationData)),
            RootedOr(variable("APPDATA"), () => knownFolder(Environment.SpecialFolder.ApplicationData)),
            RootedOr(variable("USERPROFILE"), () => knownFolder(Environment.SpecialFolder.UserProfile)),
            variable(ClaudeConfigDirVariable));
    }

    /// <summary>The account these paths serve; <see cref="AccountContext.Default"/> unless started with <c>--claude-dir</c>.</summary>
    public AccountContext Account { get; }

    /// <summary>%LOCALAPPDATA%\ClaudeTrayApp, or its <c>accounts\&lt;key&gt;</c> subfolder: cache, history database and logs.</summary>
    public string LocalRoot { get; }

    /// <summary>%APPDATA%\ClaudeTrayApp, or its <c>accounts\&lt;key&gt;</c> subfolder: the account's settings.</summary>
    public string RoamingRoot { get; }

    /// <summary>%LOCALAPPDATA%\ClaudeTrayApp whatever the account: state every account shares.</summary>
    public string SharedLocalRoot { get; }

    /// <summary>%APPDATA%\ClaudeTrayApp whatever the account: holds the settings every account shares.</summary>
    public string SharedRoamingRoot { get; }

    public string LogsDirectory => Path.Combine(LocalRoot, "logs");

    public string CacheFile => Path.Combine(LocalRoot, "cache.json");

    public string DatabaseFile => Path.Combine(LocalRoot, "history.db");

    /// <summary>When the app last asked GitHub for a newer release, and which version it already mentioned. Shared by every account.</summary>
    public string UpdateStateFile => Path.Combine(SharedLocalRoot, "update-check.json");

    /// <summary>This account's settings. For the default account it is also <see cref="GlobalSettingsFile"/>.</summary>
    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");

    /// <summary>The settings file that holds what every account shares (theme, update check).</summary>
    public string GlobalSettingsFile => Path.Combine(SharedRoamingRoot, "settings.json");

    /// <summary>Claude Code's default home, <c>%USERPROFILE%\.claude</c>, whatever this account reads.</summary>
    public string DefaultClaudeHome { get; }

    /// <summary>Claude Code home (normally ~/.claude). This app only ever reads from it.</summary>
    public string ClaudeHome { get; }

    /// <summary>Claude Code's main config file (<c>~/.claude.json</c>), which holds the account block. Read-only.</summary>
    public string ClaudeConfigFile { get; }

    /// <summary>The <c>CLAUDE_CONFIG_DIR</c> override in force, or null when Claude Code's default home applies.</summary>
    public string? ClaudeConfigDirOverride { get; }

    /// <summary>%APPDATA%\Claude\claude-code: the Claude Code builds Claude Desktop bundles, one folder per version.</summary>
    public string ClaudeDesktopCliDirectory { get; }

    public string ClaudeCredentialsFile => Path.Combine(ClaudeHome, ".credentials.json");

    public string ClaudeProjectsDirectory => Path.Combine(ClaudeHome, "projects");

    /// <summary>The same roots, serving <paramref name="account"/>.</summary>
    public AppPaths ForAccount(AccountContext account) =>
        new(_localAppData, _roamingAppData, _userProfile, _environmentOverride, account);

    private static string KnownFolder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    private static string RootedOr(string? candidate, Func<string> fallback) =>
        !string.IsNullOrWhiteSpace(candidate) && Path.IsPathRooted(candidate) ? candidate : fallback();
}
