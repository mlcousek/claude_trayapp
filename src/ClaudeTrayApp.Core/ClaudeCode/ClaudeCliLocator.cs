using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Core.ClaudeCode;

public enum ClaudeCliKind
{
    /// <summary>A native executable that can be started directly.</summary>
    Executable,

    /// <summary>An npm <c>.cmd</c>/<c>.bat</c> shim, which needs <c>cmd.exe</c> to run.</summary>
    Shim,
}

/// <summary>Where a Claude Code CLI was found and how to start it. <see cref="Source"/> is for logs and the UI.</summary>
public sealed record ClaudeCliLocation(string Path, ClaudeCliKind Kind, string Source)
{
    /// <summary>The process to start for the given CLI arguments: the executable itself, or <c>cmd.exe</c> for a shim.</summary>
    public (string FileName, IReadOnlyList<string> Arguments) Command(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var list = arguments.ToList();
        return Kind == ClaudeCliKind.Shim
            ? ("cmd.exe", ["/d", "/c", Path, .. list])
            : (Path, list);
    }
}

public interface IClaudeCliLocator
{
    /// <summary>Finds the CLI once per process. Null when neither PATH nor Claude Desktop has one.</summary>
    ClaudeCliLocation? Locate();
}

/// <summary>
/// Looks for the Claude Code CLI first on PATH (a native <c>claude.exe</c> or the npm <c>claude.cmd</c> shim), then in
/// the builds Claude Desktop bundles under <c>%APPDATA%\Claude\claude-code\&lt;version&gt;\</c>, newest version first.
/// The result, including "not found", is cached for the life of the process.
/// </summary>
public sealed class ClaudeCliLocator : IClaudeCliLocator
{
    public const string PathSource = "PATH";
    public const string DesktopSource = "Claude Desktop";
    public const string ExecutableFileName = "claude.exe";

    private static readonly string[] WindowsCandidates = [ExecutableFileName, "claude.cmd", "claude.bat"];
    private static readonly string[] OtherCandidates = ["claude"];

    private readonly string? _pathVariable;
    private readonly string _desktopCliDirectory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IEnumerable<string>> _subdirectories;
    private readonly ILogger<ClaudeCliLocator> _logger;
    private readonly Lazy<ClaudeCliLocation?> _location;

    public ClaudeCliLocator(
        string? pathVariable,
        string desktopCliDirectory,
        Func<string, bool> fileExists,
        Func<string, IEnumerable<string>> subdirectories,
        ILogger<ClaudeCliLocator> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopCliDirectory);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(subdirectories);
        ArgumentNullException.ThrowIfNull(logger);
        _pathVariable = pathVariable;
        _desktopCliDirectory = desktopCliDirectory;
        _fileExists = fileExists;
        _subdirectories = subdirectories;
        _logger = logger;
        _location = new Lazy<ClaudeCliLocation?>(Find, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The locator for the running process: the real PATH and file system, Claude Desktop's folder from <paramref name="paths"/>.</summary>
    public static ClaudeCliLocator FromEnvironment(AppPaths paths, ILogger<ClaudeCliLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ClaudeCliLocator(
            Environment.GetEnvironmentVariable("PATH"),
            paths.ClaudeDesktopCliDirectory,
            File.Exists,
            EnumerateDirectoriesSafely,
            logger);
    }

    public ClaudeCliLocation? Locate() => _location.Value;

    /// <summary>Parses a Claude Desktop build folder name such as "2.1.275" into a comparable version; null for anything else.</summary>
    internal static Version? ParseBundledVersion(string directory) =>
        Version.TryParse(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), out var version) ? version : null;

    private static List<string> EnumerateDirectoriesSafely(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private ClaudeCliLocation? Find()
    {
        var found = FindOnPath() ?? FindBundledWithDesktop();
        if (found is null)
        {
            _logger.LogDebug("Claude Code CLI not found on PATH or under {DesktopDirectory}", _desktopCliDirectory);
        }
        else
        {
            _logger.LogDebug("Claude Code CLI found at {Path} ({Source}, {Kind})", found.Path, found.Source, found.Kind);
        }

        return found;
    }

    private ClaudeCliLocation? FindOnPath()
    {
        if (string.IsNullOrWhiteSpace(_pathVariable))
        {
            return null;
        }

        var candidates = OperatingSystem.IsWindows() ? WindowsCandidates : OtherCandidates;
        foreach (var entry in _pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = entry.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                string path;
                try
                {
                    path = Path.Combine(directory, candidate);
                }
                catch (ArgumentException)
                {
                    break;
                }

                if (_fileExists(path))
                {
                    var kind = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows()
                        ? ClaudeCliKind.Executable
                        : ClaudeCliKind.Shim;
                    return new ClaudeCliLocation(path, kind, PathSource);
                }
            }
        }

        return null;
    }

    private ClaudeCliLocation? FindBundledWithDesktop()
    {
        var newestFirst = _subdirectories(_desktopCliDirectory)
            .Select(directory => (Directory: directory, Version: ParseBundledVersion(directory)))
            .Where(pair => pair.Version is not null)
            .OrderByDescending(pair => pair.Version)
            .Select(pair => pair.Directory);

        foreach (var directory in newestFirst)
        {
            var path = Path.Combine(directory, ExecutableFileName);
            if (_fileExists(path))
            {
                return new ClaudeCliLocation(path, ClaudeCliKind.Executable, DesktopSource);
            }
        }

        return null;
    }
}
