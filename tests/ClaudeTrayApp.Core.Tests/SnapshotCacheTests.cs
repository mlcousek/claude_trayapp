using ClaudeTrayApp.Core.Cache;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public sealed class SnapshotCacheTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClaudeTrayApp.Tests", Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_directory, "nested", "cache.json");

    private SnapshotCache Cache() => new(CachePath, new ListLogger<SnapshotCache>());

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Round_trips_a_snapshot_and_marks_it_as_cached()
    {
        var snapshot = new UsageSnapshot(
            [UsageWindow.Create("five_hour", 51, Now.AddHours(2)), UsageWindow.Create("seven_day", 93.5, null)],
            "max",
            null,
            new OverageInfo(true, 12.5m, 50m, 25, "USD"),
            Now,
            UsageSource.Live);

        await Cache().SaveAsync(snapshot, TestContext.Current.CancellationToken);
        var loaded = await Cache().LoadAsync(TestContext.Current.CancellationToken);

        var result = loaded.ShouldNotBeNull();
        result.Source.ShouldBe(UsageSource.Cache);
        result.Windows.Count.ShouldBe(2);
        result.Windows[0].Key.ShouldBe("five_hour");
        result.Windows[0].ResetsAt.ShouldBe(Now.AddHours(2));
        result.Windows[1].Status.ShouldBe(UsageWindowStatus.Critical);
        result.Overage.ShouldBe(snapshot.Overage);
        result.PlanTier.ShouldBe("max");
        result.LastUpdated.ShouldBe(Now);
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_cache_yet() =>
        (await Cache().LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();

    [Fact]
    public async Task Survives_a_corrupt_cache_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, "{ not json", TestContext.Current.CancellationToken);

        (await Cache().LoadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Writes_atomically_leaving_no_temporary_file()
    {
        await Cache().SaveAsync(UsageSnapshot.Empty(Now), TestContext.Current.CancellationToken);

        File.Exists(CachePath).ShouldBeTrue();
        File.Exists(CachePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task The_cache_file_never_contains_a_token_field()
    {
        await Cache().SaveAsync(UsageSnapshot.Empty(Now) with { PlanTier = "max" }, TestContext.Current.CancellationToken);

        var text = await File.ReadAllTextAsync(CachePath, TestContext.Current.CancellationToken);
        text.ShouldNotContain("token", Case.Insensitive);
    }
}
