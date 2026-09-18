using System.Globalization;
using ClaudeTrayApp.Core.ClaudeCode;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Core.Credentials;

public enum CredentialRefreshOutcome
{
    /// <summary>The credentials file now holds a token whose expiry is in the future.</summary>
    Refreshed,

    /// <summary>The CLI ran and exited but the file still holds an expired token.</summary>
    NotRefreshed,

    /// <summary>A nudge ran too recently; nothing was started.</summary>
    Throttled,

    /// <summary>No Claude Code CLI on this machine; nothing was started and nothing will be until restart.</summary>
    CliNotFound,

    /// <summary>The CLI could not be started or did not exit in time.</summary>
    Failed,
}

/// <summary>
/// Asks Claude Code to refresh its own expired credentials. Implementations never touch the credentials file
/// themselves and never call an OAuth endpoint; they only start Claude Code and read the file back.
/// </summary>
public interface ICredentialRefreshNudge
{
    Task<CredentialRefreshOutcome> TryRefreshAsync(CancellationToken cancellationToken);
}

public sealed record ClaudeCliRefreshNudgeOptions
{
    /// <summary>Where the CLI runs and where the empty MCP config is written. Must be a folder this app owns.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The <c>CLAUDE_CONFIG_DIR</c> this app was started with, forwarded so the CLI refreshes the file this app reads.</summary>
    public string? ClaudeConfigDirOverride { get; init; }

    /// <summary>Minimum gap between two nudges, whoever asks.</summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the CLI may run before it is killed. One HTTPS round trip plus a cold start fits comfortably.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Starts Claude Code headless in print mode with its input closed: Claude Code runs its startup work, which refreshes
/// an expired OAuth token and rewrites its own credentials file, then exits at end of input without ever sending a
/// prompt. Verified against Claude Code 2.1.276 on 2026-09-18; see docs/data-sources.md.
/// </summary>
public sealed class ClaudeCliRefreshNudge : ICredentialRefreshNudge, IDisposable
{
    public const string EmptyMcpConfigFileName = "empty-mcp.json";
    public const string EmptyMcpConfigContent = "{\"mcpServers\":{}}";

    /// <summary>
    /// Variables removed from the child's environment. A Claude Code started by the Desktop app or an SDK host marks its
    /// children with the first two and hands them a host token instead of reading the file; an API key makes the CLI
    /// skip OAuth entirely. Either would stop the refresh this nudge exists for.
    /// </summary>
    public static readonly string[] StrippedVariables = ["CLAUDECODE", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

    public const string StrippedVariablePrefix = "CLAUDE_CODE_";

    private readonly IClaudeCliLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly ICredentialSource _credentials;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClaudeCliRefreshNudge> _logger;
    private readonly ClaudeCliRefreshNudgeOptions _options;
    private readonly Func<IEnumerable<string>> _environmentVariableNames;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastAttempt;
    private bool _cliMissing;

    public ClaudeCliRefreshNudge(
        IClaudeCliLocator locator,
        IProcessRunner runner,
        ICredentialSource credentials,
        TimeProvider clock,
        ILogger<ClaudeCliRefreshNudge> logger,
        ClaudeCliRefreshNudgeOptions options,
        Func<IEnumerable<string>>? environmentVariableNames = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _locator = locator;
        _runner = runner;
        _credentials = credentials;
        _clock = clock;
        _logger = logger;
        _options = options;
        _environmentVariableNames = environmentVariableNames ?? CurrentEnvironmentVariableNames;
    }

    /// <summary>
    /// The CLI arguments: print mode reading stream-json from an input that is closed at once, no MCP servers beyond an
    /// empty config, no session file. <c>--bare</c> must never be added: it skips the startup work that refreshes.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string emptyMcpConfigPath) =>
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--strict-mcp-config",
        "--mcp-config", emptyMcpConfigPath,
        "--no-session-persistence",
    ];

    /// <summary>Environment overrides for the child: the stripped variables removed, the config dir forwarded when set.</summary>
    public IReadOnlyDictionary<string, string?> BuildEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _environmentVariableNames())
        {
            if (name.StartsWith(StrippedVariablePrefix, StringComparison.OrdinalIgnoreCase)
                || StrippedVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                environment[name] = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.ClaudeConfigDirOverride))
        {
            environment[AppPaths.ClaudeConfigDirVariable] = _options.ClaudeConfigDirOverride;
        }

        return environment;
    }

    public async Task<CredentialRefreshOutcome> TryRefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cliMissing)
            {
                return CredentialRefreshOutcome.CliNotFound;
            }

            var now = _clock.GetUtcNow();
            if (_lastAttempt is { } last && now - last < _options.MinimumInterval)
            {
                _logger.LogDebug("Credential refresh nudge skipped: last attempt {Seconds:0} s ago", (now - last).TotalSeconds);
                return CredentialRefreshOutcome.Throttled;
            }

            var location = _locator.Locate();
            if (location is null)
            {
                _cliMissing = true;
                _logger.LogWarning("Claude Code CLI not found; the expired sign-in cannot be refreshed until the CLI is installed or run by hand");
                return CredentialRefreshOutcome.CliNotFound;
            }

            _lastAttempt = now;
            var before = await ReadExpiryAsync(cancellationToken).ConfigureAwait(false);

            string mcpConfig;
            try
            {
                mcpConfig = await EnsureEmptyMcpConfigAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Credential refresh nudge failed: could not write {File} ({Reason})", EmptyMcpConfigFileName, ex.GetType().Name);
                return CredentialRefreshOutcome.Failed;
            }

            var (fileName, arguments) = location.Command(Arguments(mcpConfig));
            var request = new ProcessRunRequest(fileName, arguments, _options.WorkingDirectory, BuildEnvironment());
            var result = await _runner.RunAsync(request, _options.Timeout, cancellationToken).ConfigureAwait(false);

            var after = await ReadExpiryAsync(cancellationToken).ConfigureAwait(false);
            var outcome = after is { } refreshedUntil && refreshedUntil > _clock.GetUtcNow()
                ? CredentialRefreshOutcome.Refreshed
                : result.Started && !result.TimedOut ? CredentialRefreshOutcome.NotRefreshed : CredentialRefreshOutcome.Failed;

            _logger.LogInformation(
                "Credential refresh nudge via {Source}: {Outcome}; exit code {ExitCode}, {Elapsed:0} ms, expiry {Before} -> {After}",
                location.Source,
                outcome,
                result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? result.FailureReason ?? "none",
                result.Elapsed.TotalMilliseconds,
                Describe(before),
                Describe(after));
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static string Describe(DateTimeOffset? expiry) => expiry?.ToString("O", CultureInfo.InvariantCulture) ?? "none";

    private static List<string> CurrentEnvironmentVariableNames() =>
        Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList();

    private async Task<DateTimeOffset?> ReadExpiryAsync(CancellationToken cancellationToken)
    {
        var lookup = await _credentials.ReadAsync(cancellationToken).ConfigureAwait(false);
        return lookup.Status == CredentialStatus.Found ? lookup.Credentials?.ExpiresAt : null;
    }

    private async Task<string> EnsureEmptyMcpConfigAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.WorkingDirectory);
        var path = Path.Combine(_options.WorkingDirectory, EmptyMcpConfigFileName);
        if (!File.Exists(path) || await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) != EmptyMcpConfigContent)
        {
            await File.WriteAllTextAsync(path, EmptyMcpConfigContent, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }
}
