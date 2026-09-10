using System.Collections.Concurrent;
using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Storage;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

/// <summary>
/// The loop that keeps local analytics current: the initial scan, the file watcher, manual requests, the safety-net
/// rescan, debouncing, failure handling and shutdown. Runs against a real scanner and SQLite store in a temp folder;
/// timings are short and every wait is bounded, so a regression fails instead of hanging.
/// </summary>
public sealed class LocalAnalyticsProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClaudeTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;
    private readonly ConcurrentLogger<JsonlScanner> _scannerLog = new();
    private readonly ConcurrentLogger<LocalAnalyticsProvider> _providerLog = new();
    private readonly CancellationTokenSource _stop = new();
    private int _dataChanged;

    public LocalAnalyticsProviderTests()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteStore(Path.Combine(_root, "history.db"));
    }

    private string Projects => Path.Combine(_root, "projects");

    private string ProjectFolder => Path.Combine(Projects, "C--work-alpha");

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_blank_projects_directory_is_refused() =>
        Should.Throw<ArgumentException>(() => new LocalAnalyticsProvider(Scanner(), "  ", _providerLog));

    [Fact]
    public async Task The_initial_scan_announces_itself_even_when_it_finds_nothing()
    {
        Directory.CreateDirectory(ProjectFolder);
        using var provider = Provider(debounce: TimeSpan.FromMilliseconds(10), safety: Never);

        var run = Start(provider);
        await Eventually(() => _dataChanged == 1, "the initial DataChanged");

        provider.LastScan.ShouldNotBeNull().NewEvents.ShouldBe(0);
        provider.ProjectsDirectoryExists.ShouldBeTrue();
        await StopAsync(run);
    }

    [Fact]
    public async Task A_new_session_log_is_picked_up_by_the_watcher()
    {
        Directory.CreateDirectory(ProjectFolder);
        using var provider = Provider(debounce: TimeSpan.FromMilliseconds(50), safety: Never);
        var run = Start(provider);
        await Eventually(() => _dataChanged == 1, "the initial scan");

        WriteSession("fresh.jsonl", "t1", "t2");

        await Eventually(() => _dataChanged == 2, "a rescan triggered by the watcher");
        _store.CountEvents().ShouldBe(2);
        provider.LastScan.ShouldNotBeNull().NewEvents.ShouldBe(2);
        await StopAsync(run);
    }

    [Fact]
    public async Task RequestScan_rescans_without_the_watcher_or_the_safety_net()
    {
        // No folder at start means no watcher, and the safety net is an hour away: only the request can explain a scan.
        using var provider = Provider(debounce: TimeSpan.FromMilliseconds(10), safety: Never);
        var run = Start(provider);
        await Eventually(() => _dataChanged == 1, "the initial scan");

        WriteSession("later.jsonl", "t1");
        provider.RequestScan();

        await Eventually(() => _dataChanged == 2, "the requested rescan");
        _store.CountEvents().ShouldBe(1);
        await StopAsync(run);
    }

    [Fact]
    public async Task Without_a_projects_folder_the_safety_net_keeps_scanning_until_one_appears()
    {
        using var provider = Provider(debounce: TimeSpan.FromMilliseconds(10), safety: TimeSpan.FromMilliseconds(100));
        var run = Start(provider);
        await Eventually(() => _dataChanged == 1, "the initial scan");
        provider.ProjectsDirectoryExists.ShouldBeFalse();
        _providerLog.Contains("No Claude Code session logs").ShouldBeTrue();

        WriteSession("first.jsonl", "t1");

        await Eventually(() => _dataChanged == 2, "the safety-net rescan");
        provider.ProjectsDirectoryExists.ShouldBeTrue();
        _store.CountEvents().ShouldBe(1);
        await StopAsync(run);
    }

    [Fact]
    public async Task Several_requests_in_quick_succession_become_one_scan()
    {
        WriteSession("existing.jsonl", "t1");
        using var provider = Provider(debounce: TimeSpan.FromMilliseconds(300), safety: Never);
        var run = Start(provider);
        await Eventually(() => ScanCount == 1, "the initial scan");

        for (var i = 0; i < 5; i++)
        {
            provider.RequestScan();
        }

        await Eventually(() => ScanCount == 2, "the debounced rescan");
        await Task.Delay(TimeSpan.FromMilliseconds(700), TestContext.Current.CancellationToken);
        ScanCount.ShouldBe(2);
        await StopAsync(run);
    }

    [Fact]
    public async Task A_rescan_that_adds_nothing_does_not_raise_DataChanged()
    {
        WriteSession("existing.jsonl", "t1");
        using var provider = Provider(debounce: TimeSpan.FromMilliseconds(10), safety: Never);
        var run = Start(provider);
        await Eventually(() => ScanCount == 1 && _dataChanged == 1, "the initial scan");

        provider.RequestScan();

        await Eventually(() => ScanCount == 2, "the requested rescan");
        _dataChanged.ShouldBe(1);
        await StopAsync(run);
    }

    [Fact]
    public async Task A_failing_scan_is_logged_and_the_loop_keeps_going()
    {
        WriteSession("existing.jsonl", "t1");
        var broken = Substitute.For<IAnalyticsStore>();
        broken.GetScanState(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("store unavailable"));
        var scanner = new JsonlScanner(Projects, broken, new FakeTimeProvider(Now), _scannerLog);
        using var provider = new LocalAnalyticsProvider(scanner, Projects, _providerLog, TimeSpan.FromMilliseconds(10), Never);

        var run = Start(provider);
        await Eventually(() => _providerLog.Count("Session log scan failed") == 1, "the first failure to be logged");

        provider.RequestScan();

        await Eventually(() => _providerLog.Count("Session log scan failed") == 2, "a second attempt after the failure");
        provider.LastScan.ShouldBeNull();
        _dataChanged.ShouldBe(0);
        run.IsCompleted.ShouldBeFalse();
        await StopAsync(run);
    }

    [Fact]
    public async Task Stopping_ends_the_loop_promptly_even_mid_wait()
    {
        Directory.CreateDirectory(ProjectFolder);
        using var provider = Provider(debounce: TimeSpan.FromMilliseconds(10), safety: Never);
        var run = Start(provider);
        await Eventually(() => _dataChanged == 1, "the initial scan");

        await _stop.CancelAsync();

        var error = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        error.ShouldBeAssignableTo<OperationCanceledException>();
    }

    private int ScanCount => _scannerLog.Count("Session log scan:");

    private JsonlScanner Scanner() => new(Projects, _store, new FakeTimeProvider(Now), _scannerLog);

    private LocalAnalyticsProvider Provider(TimeSpan debounce, TimeSpan safety)
    {
        var provider = new LocalAnalyticsProvider(Scanner(), Projects, _providerLog, debounce, safety);
        provider.DataChanged += (_, _) => Interlocked.Increment(ref _dataChanged);
        return provider;
    }

    private Task Start(LocalAnalyticsProvider provider) => Task.Run(() => provider.RunAsync(_stop.Token));

    private async Task StopAsync(Task run)
    {
        await _stop.CancelAsync();
        var error = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        error.ShouldBeAssignableTo<OperationCanceledException>();
    }

    /// <summary>Writes complete assistant lines with the given ids; each id is one distinct, deduplicable event.</summary>
    private void WriteSession(string fileName, params string[] ids)
    {
        Directory.CreateDirectory(ProjectFolder);
        var lines = ids.Select(id => """
            {"type":"assistant","uuid":"a-@ID@","timestamp":"2026-09-07T05:44:13.000Z","sessionId":"s1","requestId":"req_@ID@","cwd":"C:/work/alpha","message":{"id":"msg_@ID@","type":"message","role":"assistant","model":"claude-fable-5-1","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":5}}}
            """.Replace("@ID@", id, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(ProjectFolder, fileName), string.Join("\n", lines) + "\n");
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A logger the background loop can write to while the test thread reads it.</summary>
    private sealed class ConcurrentLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public int Count(string fragment) => _entries.Count(e => e.Contains(fragment, StringComparison.Ordinal));

        public bool Contains(string fragment) => Count(fragment) > 0;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(formatter(state, exception));
    }
}
