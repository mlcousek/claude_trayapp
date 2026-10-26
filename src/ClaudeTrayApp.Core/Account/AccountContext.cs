using System.Security.Cryptography;
using System.Text;

namespace ClaudeTrayApp.Core.Account;

/// <summary>
/// Which Claude account this process serves. Without <c>--claude-dir</c> it is the default account, which behaves
/// exactly as a single-account install always has. With it, the process reads that Claude home and keeps its own data,
/// settings, single-instance names and startup entry, all named by <see cref="Key"/>.
/// </summary>
public sealed class AccountContext
{
    public const string ClaudeDirSwitch = "--claude-dir";
    public const string LabelSwitch = "--label";

    /// <summary>What a valid invocation looks like, for argument errors.</summary>
    public const string Usage = "ClaudeTrayApp.exe --claude-dir \"C:\\Users\\you\\.claude-work\" [--label \"Work\"]";

    private const int MaxSlugLength = 24;

    private AccountContext(string? claudeHome, string? key, string? labelArgument)
    {
        ClaudeHome = claudeHome;
        Key = key;
        LabelArgument = labelArgument;
    }

    public static AccountContext Default { get; } = new(null, null, null);

    /// <summary>True for the account started without <c>--claude-dir</c> (or with the default home).</summary>
    public bool IsDefault => ClaudeHome is null;

    /// <summary>The normalised Claude home given by <c>--claude-dir</c>; null for the default account.</summary>
    public string? ClaudeHome { get; }

    /// <summary>"claude-work-3f9a1c07": names this account's folders, mutex, startup entry. Null for the default account.</summary>
    public string? Key { get; }

    /// <summary>The <c>--label</c> text as given (sanitised), or null. Only ever set for a non-default account.</summary>
    public string? LabelArgument { get; }

    /// <summary>
    /// Reads <c>--claude-dir</c> and <c>--label</c> from the command line. Other arguments are left to their own
    /// parsers. Returns false with a user-facing reason when the arguments are unusable; the caller must not fall back
    /// to the default account then.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, string defaultClaudeHome, out AccountContext context, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultClaudeHome);
        context = Default;

        if (!TryReadValue(args, ClaudeDirSwitch, "a folder path", out var directory, out error)
            || !TryReadValue(args, LabelSwitch, "a name", out var label, out error))
        {
            return false;
        }

        if (directory is null)
        {
            return true;
        }

        if (!TryNormalize(directory, out var home))
        {
            error = $"{ClaudeDirSwitch} \"{directory}\" is not a usable folder path.";
            return false;
        }

        if (string.Equals(home, Normalize(defaultClaudeHome), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        context = new AccountContext(home, KeyFor(home), AccountLabel.Sanitize(label));
        return true;
    }

    /// <summary>Readable slug of the folder name plus 8 hex digits of a hash of the full path, so equal names never collide.</summary>
    internal static string KeyFor(string normalizedHome)
    {
        var name = Path.GetFileName(normalizedHome).TrimStart('.').ToLowerInvariant();
        var slug = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        if (text.Length > MaxSlugLength)
        {
            text = text[..MaxSlugLength].TrimEnd('-');
        }

        if (text.Length == 0)
        {
            text = "account";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedHome.ToUpperInvariant()));
        return text + "-" + Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool TryNormalize(string path, out string normalized)
    {
        normalized = string.Empty;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0)
        {
            return false;
        }

        try
        {
            normalized = Normalize(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryReadValue(IReadOnlyList<string> args, string switchName, string what, out string? value, out string? error)
    {
        value = null;
        error = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], switchName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value is not null)
            {
                error = $"{switchName} was given more than once.";
                return false;
            }

            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{switchName} needs {what} after it.";
                return false;
            }

            value = args[++i];
        }

        return true;
    }
}
