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

    /// <summary>Builds the paths for the current user from well-known environment folders.</summary>
    public static AppPaths FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        Environment.GetEnvironmentVariable(ClaudeConfigDirVariable));

    /// <summary>%LOCALAPPDATA%\ClaudeTrayApp: cache, history database and logs.</summary>
    public string LocalRoot { get; }

    /// <summary>%APPDATA%\ClaudeTrayApp: user settings.</summary>
    public string RoamingRoot { get; }

    public string LogsDirectory => Path.Combine(LocalRoot, "logs");

    public string CacheFile => Path.Combine(LocalRoot, "cache.json");

    public string DatabaseFile => Path.Combine(LocalRoot, "history.db");

    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");

    /// <summary>Claude Code home (normally ~/.claude). This app only ever reads from it.</summary>
    public string ClaudeHome { get; }

    /// <summary>Claude Code's main config file (<c>~/.claude.json</c>), which holds the account block. Read-only.</summary>
    public string ClaudeConfigFile { get; }

    public string ClaudeCredentialsFile => Path.Combine(ClaudeHome, ".credentials.json");

    public string ClaudeProjectsDirectory => Path.Combine(ClaudeHome, "projects");
}
