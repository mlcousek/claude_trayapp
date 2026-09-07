using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Polling;

namespace ClaudeTrayApp.Core.Aggregation;

/// <summary>
/// The one object the UI reads. Percentages and resets come from the endpoint (or its cache) and are never derived
/// locally; tokens, cost and burn rate come from the session logs. Each side names its source.
/// </summary>
public sealed record AggregatedUsage(PollStatus Poll, LocalAnalytics? Local, DateTimeOffset Now)
{
    public const string AnalyticsSource = "local session logs";

    public bool PercentagesAvailable => Poll.HasPercentages;

    public bool AnalyticsAvailable => Local is not null;

    public string PercentagesSource => Poll.Snapshot?.Source == UsageSource.Cache ? "usage endpoint, cached" : "usage endpoint";

    public UsageSnapshot? Snapshot => Poll.Snapshot;
}

public static class UsageAggregator
{
    public static AggregatedUsage Combine(PollStatus poll, LocalAnalytics? local, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(poll);
        return new AggregatedUsage(poll, local, now);
    }
}
