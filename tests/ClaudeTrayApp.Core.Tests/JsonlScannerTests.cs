using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Storage;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public sealed class JsonlScannerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ClaudeTrayApp.Tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;
    private readonly string _logPath;

    public JsonlScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "projects", "C--work-alpha"));
        _logPath = Path.Combine(_root, "projects", "C--work-alpha", "session.jsonl");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sessions", "sample.jsonl"), _logPath);
        _store = new SqliteStore(Path.Combine(_root, "history.db"));
    }

    private JsonlScanner Scanner() => new(Path.Combine(_root, "projects"), _store, new FakeTimeProvider(Now), new ListLogger<JsonlScanner>());

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void First_scan_stores_distinct_messages_and_leaves_the_partial_tail()
    {
        var result = Scanner().Scan(TestContext.Current.CancellationToken);

        result.Files.ShouldBe(1);
        result.ChangedFiles.ShouldBe(1);
        result.NewEvents.ShouldBe(3);
        _store.CountEvents().ShouldBe(3);
        var state = _store.GetScanState(_logPath).ShouldNotBeNull();
        state.Offset.ShouldBeLessThan(new FileInfo(_logPath).Length);
    }

    [Fact]
    public void Second_scan_of_an_unchanged_file_reads_nothing()
    {
        Scanner().Scan(TestContext.Current.CancellationToken);

        var result = Scanner().Scan(TestContext.Current.CancellationToken);

        result.ChangedFiles.ShouldBe(0);
        result.NewEvents.ShouldBe(0);
    }

    [Fact]
    public void Completing_the_partial_line_and_appending_reads_only_the_new_bytes()
    {
        Scanner().Scan(TestContext.Current.CancellationToken);
        var before = _store.GetScanState(_logPath)!.Offset;

        File.AppendAllText(_logPath, "ens\":2}}}\n" + """{"type":"assistant","uuid":"a7","timestamp":"2026-09-07T06:20:00Z","requestId":"req_9","message":{"id":"msg_9","role":"assistant","model":"claude-opus-5","usage":{"input_tokens":3,"output_tokens":4}}}""" + "\n");
        File.SetLastWriteTimeUtc(_logPath, DateTime.UtcNow.AddSeconds(5));

        var result = Scanner().Scan(TestContext.Current.CancellationToken);

        result.NewEvents.ShouldBe(2);
        _store.GetScanState(_logPath)!.Offset.ShouldBeGreaterThan(before);
        _store.GetScanState(_logPath)!.Offset.ShouldBe(new FileInfo(_logPath).Length);
    }

    [Fact]
    public void A_shrunken_file_is_read_again_from_the_start()
    {
        Scanner().Scan(TestContext.Current.CancellationToken);

        File.WriteAllText(_logPath, """{"type":"assistant","uuid":"b1","timestamp":"2026-09-07T07:00:00Z","requestId":"r","message":{"id":"msg_new","role":"assistant","model":"claude-opus-5","usage":{"input_tokens":1,"output_tokens":1}}}""" + "\n");
        File.SetLastWriteTimeUtc(_logPath, DateTime.UtcNow.AddSeconds(5));

        var result = Scanner().Scan(TestContext.Current.CancellationToken);

        result.ChangedFiles.ShouldBe(1);
        result.NewEvents.ShouldBe(1);
        _store.GetScanState(_logPath)!.Offset.ShouldBe(new FileInfo(_logPath).Length);
    }

    [Fact]
    public void Files_older_than_the_retention_window_are_skipped_on_first_sight()
    {
        File.SetLastWriteTimeUtc(_logPath, Now.UtcDateTime.AddDays(-120));
        var scanner = new JsonlScanner(Path.Combine(_root, "projects"), _store, new FakeTimeProvider(Now), new ListLogger<JsonlScanner>(), TimeSpan.FromDays(90));

        var result = scanner.Scan(TestContext.Current.CancellationToken);

        result.NewEvents.ShouldBe(0);
        _store.GetScanState(_logPath).ShouldBeNull();
    }

    [Fact]
    public void A_missing_projects_folder_is_not_an_error()
    {
        var scanner = new JsonlScanner(Path.Combine(_root, "nope"), _store, new FakeTimeProvider(Now), new ListLogger<JsonlScanner>());

        scanner.Scan(TestContext.Current.CancellationToken).ShouldBe(ScanResult.Empty);
    }
}
