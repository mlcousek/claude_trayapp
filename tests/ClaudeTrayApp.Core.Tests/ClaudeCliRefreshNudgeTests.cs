using ClaudeTrayApp.Core.ClaudeCode;
using ClaudeTrayApp.Core.Credentials;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public sealed class ClaudeCliRefreshNudgeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expired = Now - TimeSpan.FromHours(1);
    private static readonly DateTimeOffset Fresh = Now + TimeSpan.FromHours(8);

    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "ClaudeTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeProcessRunner _runner = new();
    private readonly ListLogger<ClaudeCliRefreshNudge> _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Reports_refreshed_when_the_file_expiry_moves_into_the_future()
    {
        var credentials = new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired), ScriptedCredentialSource.ExpiredAt(Fresh));
        using var nudge = Create(FakeCliLocator.Native(), credentials);

        var outcome = await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        outcome.ShouldBe(CredentialRefreshOutcome.Refreshed);
        _runner.Runs.Count.ShouldBe(1);
        credentials.Reads.ShouldBe(2);
        _log.All.ShouldContain("Refreshed");
        _log.All.ShouldContain("exit code 0");
        _log.All.ShouldContain(Expired.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _log.All.ShouldContain(Fresh.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Reports_not_refreshed_when_the_cli_exits_but_the_token_stays_expired()
    {
        using var nudge = Create(FakeCliLocator.Native(), new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired)));

        var outcome = await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        outcome.ShouldBe(CredentialRefreshOutcome.NotRefreshed);
        _log.All.ShouldContain("NotRefreshed");
    }

    [Fact]
    public async Task Starts_the_cli_headless_with_the_verified_arguments()
    {
        using var nudge = Create(FakeCliLocator.Native(), new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired)));

        await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        var (request, timeout) = _runner.Runs.ShouldHaveSingleItem();
        request.FileName.ShouldBe(Path.Combine("C:", "tools", "claude.exe"));
        request.WorkingDirectory.ShouldBe(_workingDirectory);
        timeout.ShouldBe(TimeSpan.FromSeconds(10));
        request.Arguments.ShouldContain("-p");
        request.Arguments.ShouldContain("--no-session-persistence");
        request.Arguments.ShouldContain("--strict-mcp-config");
        request.Arguments.ShouldNotContain("--bare");
        var mcpConfig = request.Arguments.SkipWhile(a => a != "--mcp-config").Skip(1).First();
        mcpConfig.ShouldBe(Path.Combine(_workingDirectory, ClaudeCliRefreshNudge.EmptyMcpConfigFileName));
        File.ReadAllText(mcpConfig).ShouldBe("{\"mcpServers\":{}}");
    }

    [Fact]
    public async Task Strips_host_and_api_key_variables_and_forwards_the_config_dir()
    {
        var names = new[] { "PATH", "CLAUDECODE", "CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH", "claude_code_entrypoint", "ANTHROPIC_API_KEY", "HOME" };
        using var nudge = Create(
            FakeCliLocator.Native(),
            new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired)),
            configDir: Path.Combine("D:", "claude-home"),
            environmentNames: names);

        await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        var environment = _runner.Runs.ShouldHaveSingleItem().Request.Environment;
        environment.Keys.ShouldBe(["CLAUDECODE", "CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH", "claude_code_entrypoint", "ANTHROPIC_API_KEY", "CLAUDE_CONFIG_DIR"], ignoreOrder: true);
        environment["CLAUDECODE"].ShouldBeNull();
        environment["ANTHROPIC_API_KEY"].ShouldBeNull();
        environment["CLAUDE_CONFIG_DIR"].ShouldBe(Path.Combine("D:", "claude-home"));
    }

    [Fact]
    public async Task Does_not_forward_a_config_dir_that_was_never_set()
    {
        using var nudge = Create(FakeCliLocator.Native(), new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired)), environmentNames: ["PATH"]);

        await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        _runner.Runs.ShouldHaveSingleItem().Request.Environment.ShouldBeEmpty();
    }

    [Fact]
    public async Task Throttles_a_second_attempt_inside_the_minimum_interval()
    {
        using var nudge = Create(FakeCliLocator.Native(), new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired)));

        await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var second = await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(31));
        var third = await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        second.ShouldBe(CredentialRefreshOutcome.Throttled);
        third.ShouldBe(CredentialRefreshOutcome.NotRefreshed);
        _runner.Runs.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_missing_cli_is_reported_once_and_never_probed_again()
    {
        var locator = FakeCliLocator.Missing();
        using var nudge = Create(locator, new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired)));

        var first = await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(10));
        var second = await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        first.ShouldBe(CredentialRefreshOutcome.CliNotFound);
        second.ShouldBe(CredentialRefreshOutcome.CliNotFound);
        locator.Calls.ShouldBe(1);
        _runner.Runs.ShouldBeEmpty();
        _log.Entries.Count(e => e.Contains("not found", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cli_that_times_out_or_does_not_start_is_a_failure(bool timedOut)
    {
        _runner.Result = new ProcessRunResult(null, timedOut, TimeSpan.FromSeconds(timedOut ? 10 : 0), timedOut ? "timed out" : "Win32Exception");
        using var nudge = Create(FakeCliLocator.Native(), new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired)));

        var outcome = await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        outcome.ShouldBe(CredentialRefreshOutcome.Failed);
        _log.All.ShouldContain(timedOut ? "timed out" : "Win32Exception");
    }

    [Fact]
    public async Task Never_logs_the_token()
    {
        var credentials = new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired), ScriptedCredentialSource.ExpiredAt(Fresh));
        using var nudge = Create(FakeCliLocator.Native(), credentials);

        await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        _log.All.ShouldNotContain(FakeCredentialSource.Token);
    }

    [Fact]
    public async Task Counts_a_refresh_only_when_the_new_expiry_is_in_the_future()
    {
        // The file changed, but to another past expiry (Claude Code marks credentials invalid with expiresAt 0).
        var credentials = new ScriptedCredentialSource(ScriptedCredentialSource.ExpiredAt(Expired), ScriptedCredentialSource.ExpiredAt(DateTimeOffset.UnixEpoch));
        using var nudge = Create(FakeCliLocator.Native(), credentials);

        var outcome = await nudge.TryRefreshAsync(TestContext.Current.CancellationToken);

        outcome.ShouldBe(CredentialRefreshOutcome.NotRefreshed);
    }

    private ClaudeCliRefreshNudge Create(
        IClaudeCliLocator locator,
        ICredentialSource credentials,
        string? configDir = null,
        IEnumerable<string>? environmentNames = null) =>
        new(
            locator,
            _runner,
            credentials,
            _clock,
            _log,
            new ClaudeCliRefreshNudgeOptions { WorkingDirectory = _workingDirectory, ClaudeConfigDirOverride = configDir },
            () => environmentNames ?? []);
}
