using System.IO;
using System.Security;
using System.Text;
using ClaudeTrayApp.Core.Account;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClaudeTrayApp.Startup;

/// <summary>
/// The per-user "Start with Windows" entry: a value under HKCU\...\Run pointing at this executable. On by default,
/// switched off from the settings window or the tray menu, and never needs elevation. Each account has its own value
/// and its own once-only marker; an account started with <c>--claude-dir</c> stores that argument in its command.
/// </summary>
public sealed class AutostartManager : IAutostartEntry
{
    public const string ValueName = "ClaudeUsageTray";
    internal const string DefaultAppliedValueName = "StartupDefaultApplied";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppKeyPath = @"Software\ClaudeUsageTray";

    private readonly string _executablePath;
    private readonly AccountContext _account;
    private readonly ILogger<AutostartManager> _logger;

    public AutostartManager(string executablePath, ILogger<AutostartManager> logger, AccountContext? account = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        _account = account ?? AccountContext.Default;
        _logger = logger;
    }

    /// <summary>The command the Run entry holds when enabled: the quoted path of this executable, plus the account's arguments.</summary>
    public string Command => BuildCommand(_executablePath, _account);

    /// <summary>"ClaudeUsageTray" for the default account, "ClaudeUsageTray.&lt;key&gt;" for the others.</summary>
    public string RunValueName => RunValueNameFor(_account);

    private string MarkerKeyPath => MarkerKeyPathFor(_account);

    /// <summary>True when the Run entry exists and points at this executable.</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return Matches(key?.GetValue(RunValueName) as string, _executablePath);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Run key could not be read");
            return false;
        }
    }

    /// <summary>Creates or removes the Run entry. Returns false with a user-facing reason when the registry refuses.</summary>
    public bool TrySet(bool enabled, out string? reason)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(RunValueName, Command, RegistryValueKind.String);
                _logger.LogInformation("Start with Windows enabled: {Command}", Command);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                _logger.LogInformation("Start with Windows disabled");
            }

            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Run key could not be changed");
            reason = "Windows refused to change the startup entry: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether the start-with-Windows default has already been applied once. The mark lives in the registry beside
    /// the entry it guards rather than in settings.json, so a settings file that is missing or unreadable for a
    /// moment can never talk the app into re-creating a startup entry the user deliberately removed.
    /// </summary>
    public bool WasDefaultApplied()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MarkerKeyPath);
            return key?.GetValue(DefaultAppliedValueName) is int and not 0;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            // Unreadable reads as "already applied": the cost of not enabling autostart is far smaller than the cost
            // of forcing it back on at every launch.
            _logger.LogWarning(ex, "The startup default marker could not be read");
            return true;
        }
    }

    /// <summary>Records that the default has been applied. A refusal only means it is attempted again next launch.</summary>
    public void MarkDefaultApplied()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(MarkerKeyPath, writable: true);
            key.SetValue(DefaultAppliedValueName, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The startup default marker could not be written");
        }
    }

    internal static string BuildCommand(string executablePath) => "\"" + executablePath + "\"";

    /// <summary>
    /// The default account's command is the bare quoted exe, as it always was. Another account adds its
    /// <c>--claude-dir</c>, and <c>--label</c> only when the instance was started with one, so the entry reproduces
    /// exactly the invocation the user chose.
    /// </summary>
    internal static string BuildCommand(string executablePath, AccountContext account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.ClaudeHome is not { } home)
        {
            return BuildCommand(executablePath);
        }

        var command = new StringBuilder(BuildCommand(executablePath))
            .Append(' ').Append(AccountContext.ClaudeDirSwitch).Append(' ').Append(QuoteArgument(home));
        if (account.LabelArgument is { } label)
        {
            command.Append(' ').Append(AccountContext.LabelSwitch).Append(' ').Append(QuoteArgument(label));
        }

        return command.ToString();
    }

    internal static string RunValueNameFor(AccountContext account) => account.Key is { } key ? ValueName + "." + key : ValueName;

    internal static string MarkerKeyPathFor(AccountContext account) => account.Key is { } key ? AppKeyPath + @"\Accounts\" + key : AppKeyPath;

    /// <summary>
    /// Quotes one argument for the Windows command-line parser. Values never contain a double quote (paths cannot, labels
    /// are stripped of them), so only trailing backslashes need doubling: <c>"C:\"</c> would otherwise escape its own
    /// closing quote.
    /// </summary>
    internal static string QuoteArgument(string value)
    {
        var trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }

    /// <summary>A stored value counts when its executable (with or without quotes) is this one, case-insensitively.</summary>
    internal static bool Matches(string? storedValue, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return false;
        }

        var value = storedValue.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            value = end > 0 ? value[1..end] : value.Trim('"');
        }
        else
        {
            var space = value.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                value = value[..space];
            }
        }

        return string.Equals(value, executablePath, StringComparison.OrdinalIgnoreCase);
    }
}
