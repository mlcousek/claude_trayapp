using ClaudeTrayApp.Core.Cache;
using ClaudeTrayApp.Core.Domain;

namespace ClaudeTrayApp.Core.Tests.TestSupport;

internal sealed class InMemorySnapshotCache : ISnapshotCache
{
    public UsageSnapshot? Stored { get; set; }

    public int Saves { get; private set; }

    public Task<UsageSnapshot?> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Stored is null ? null : Stored with { Source = UsageSource.Cache });

    public Task SaveAsync(UsageSnapshot snapshot, CancellationToken cancellationToken)
    {
        Stored = snapshot;
        Saves++;
        return Task.CompletedTask;
    }
}
