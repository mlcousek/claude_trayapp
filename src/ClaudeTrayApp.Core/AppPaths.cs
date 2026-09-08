namespace ClaudeTrayApp.Core;

/// <summary>
/// Resolves every folder and file the app touches. Nothing here is tied to a particular user:
/// all roots come from the environment, and the Claude Code home honours the
/// <c>CLAUDE_CONFIG_DIR</c> override that Claude Code itself respects.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Folder name used under both %LOCALAPPDATA% and %APPDATA%.</summary>
    public const string AppFolderName = "ClaudeTrayApp";

    /// <summary>Environment variable Claude Code uses to relocate its home directory.</summary>
    public const string ClaudeConfigDirVariable = "CLAUDE_CONFIG_DIR";

    public AppPaths(string localAppData, string roamingAppData, string userProfile, string? claudeConfigDirOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);

        LocalRoot = Path.Combine(localAppData, AppFolderName);
        RoamingRoot = Path.Combine(roamingAppData, AppFolderName);
        ClaudeHome = string.IsNullOrWhiteSpace(claudeConfigDirOverride)
            ? Path.Combine(userProfile, ".claude")
            : claudeConfigDirOverride;
        ClaudeConfigFile = Path.Combine(string.IsNullOrWhiteSpace(claudeConfigDirOverride) ? userProfile : claudeConfigDirOverride, ".claude.json");
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

    /// <summary>%LOCALAPPDATA%\ClaudeTrayApp: cache, history database and logs.</summary>
    public string LocalRoot { get; }

    /// <summary>%APPDATA%\ClaudeTrayApp: user settings.</summary>
    public string RoamingRoot { get; }

    public string LogsDirectory => Path.Combine(LocalRoot, "logs");

    public string CacheFile => Path.Combine(LocalRoot, "cache.json");

    public string DatabaseFile => Path.Combine(LocalRoot, "history.db");

    /// <summary>When the app last asked GitHub for a newer release, and which version it already mentioned.</summary>
    public string UpdateStateFile => Path.Combine(LocalRoot, "update-check.json");

    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");

    /// <summary>Claude Code home (normally ~/.claude). This app only ever reads from it.</summary>
    public string ClaudeHome { get; }

    /// <summary>Claude Code's main config file (<c>~/.claude.json</c>), which holds the account block. Read-only.</summary>
    public string ClaudeConfigFile { get; }

    public string ClaudeCredentialsFile => Path.Combine(ClaudeHome, ".credentials.json");

    public string ClaudeProjectsDirectory => Path.Combine(ClaudeHome, "projects");

    private static string KnownFolder(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

    private static string RootedOr(string? candidate, Func<string> fallback) =>
        !string.IsNullOrWhiteSpace(candidate) && Path.IsPathRooted(candidate) ? candidate : fallback();
}
