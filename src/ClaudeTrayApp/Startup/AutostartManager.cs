using System.IO;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClaudeTrayApp.Startup;

/// <summary>
/// The per-user "Start with Windows" entry: a value under HKCU\...\Run pointing at this executable. Opt-in, never
/// written unless the user asks, and never needs elevation.
/// </summary>
public sealed class AutostartManager
{
    public const string ValueName = "ClaudeUsageTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _executablePath;
    private readonly ILogger<AutostartManager> _logger;

    public AutostartManager(string executablePath, ILogger<AutostartManager> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = Path.GetFullPath(executablePath);
        _logger = logger;
    }

    /// <summary>The command the Run entry holds when enabled: the quoted path of this executable.</summary>
    public string Command => BuildCommand(_executablePath);

    /// <summary>True when the Run entry exists and points at this executable.</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return Matches(key?.GetValue(ValueName) as string, _executablePath);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Run key could not be read");
            return false;
        }
    }

    /// <summary>Creates or removes the Run entry. Returns false with a user-facing reason when the registry refuses.</summary>
    public bool TrySet(bool enabled, out string? error)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
                _logger.LogInformation("Start with Windows enabled: {Command}", Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.LogInformation("Start with Windows disabled");
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Run key could not be changed");
            error = "Windows refused to change the startup entry: " + ex.Message;
            return false;
        }
    }

    internal static string BuildCommand(string executablePath) => "\"" + executablePath + "\"";

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
