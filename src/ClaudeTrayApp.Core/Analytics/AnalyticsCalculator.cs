using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Pricing;
using ClaudeTrayApp.Core.Storage;

namespace ClaudeTrayApp.Core.Analytics;

/// <summary>
/// Derives today's totals, top projects and the current 5-hour block from the store. Block timing comes from the
/// endpoint's reset time when known; the projection uses the endpoint's percentage, never a locally invented one.
/// </summary>
public sealed class AnalyticsCalculator
{
    public static readonly TimeSpan BlockLength = TimeSpan.FromHours(5);
    private static readonly TimeSpan MinimumElapsedForRate = TimeSpan.FromMinutes(3);
    private readonly IAnalyticsStore _store;
    private readonly Func<PricingTable> _pricing;
    private readonly TimeZoneInfo _zone;

    public AnalyticsCalculator(IAnalyticsStore store, Func<PricingTable> pricing, TimeZoneInfo? zone = null)
    {
        _store = store;
        _pricing = pricing;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public LocalAnalytics Compute(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var pricing = _pricing();
        var localNow = TimeZoneInfo.ConvertTime(now, _zone);
        var dayStart = LocalMidnightUtc(localNow.Date);

        var today = Period(dayStart, now, pricing);
        var projects = _store.TopProjects(dayStart, now, 3);
        var block = ComputeBlock(snapshot, now, pricing);
        return new LocalAnalytics(now, today, projects, block, pricing, _store.CountEvents());
    }

    /// <summary>
    /// Totals per local day, used by the daily chart. Known limitation: the store buckets the whole
    /// <paramref name="from"/>-<paramref name="to"/> range (up to 30 days) using one fixed UTC offset computed at
    /// <paramref name="to"/>, so rows within a day or two of a DST transition can land in the wrong local day.
    /// </summary>
    public IReadOnlyList<DailyModelTotals> Daily(DateTimeOffset from, DateTimeOffset to) =>
        _store.DailyTotals(from, to, _zone.GetUtcOffset(to));

    /// <summary>UTC instant of local midnight on <paramref name="localDate"/>, resolved in <see cref="_zone"/> so a
    /// DST transition between midnight and "now" cannot shift the day boundary (unlike reusing now's offset).</summary>
    private DateTimeOffset LocalMidnightUtc(DateTime localDate)
    {
        var midnightUnspecified = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnightUnspecified, _zone), TimeSpan.Zero);
    }

    internal PeriodAnalytics Period(DateTimeOffset from, DateTimeOffset to, PricingTable pricing)
    {
        var byModel = _store.TotalsByModel(from, to)
            .Select(t => new ModelUsage(t.Model, t.Tokens, pricing.Cost(t.Model, t.Tokens)))
            .ToList();

        var tokens = byModel.Aggregate(TokenTotals.Zero, (sum, m) => sum.Add(m.Tokens));
        var known = byModel.Where(m => m.Cost is not null).ToList();
        var unknownWithTokens = byModel.Any(m => m.Cost is null && m.Tokens.Total > 0);
        decimal? cost = known.Count > 0 ? known.Sum(m => m.Cost!.Value) : null;
        return new PeriodAnalytics(from, to, tokens, cost, unknownWithTokens, byModel);
    }

    internal BlockAnalytics? ComputeBlock(UsageSnapshot? snapshot, DateTimeOffset now, PricingTable pricing)
    {
        var window = snapshot?.FindWindow(WindowKeys.FiveHour);
        var resetKnown = window?.ResetsAt is { } resets && resets > now && resets - now <= BlockLength;
        var end = resetKnown ? window!.ResetsAt!.Value : now;
        var start = end - BlockLength;
        if (start > now)
        {
            start = now;
        }

        var period = Period(start, now, pricing);
        var elapsed = now - start;
        if (period.Tokens.Total == 0 && !resetKnown)
        {
            return null;
        }

        var hours = Math.Max(elapsed.TotalHours, 1.0 / 60);
        var tokensPerHour = period.Tokens.Total / hours;

        double? percentPerHour = null;
        DateTimeOffset? projected = null;
        if (resetKnown && window!.UtilizationPercent > 0 && elapsed >= MinimumElapsedForRate)
        {
            percentPerHour = window.UtilizationPercent / hours;
            projected = window.UtilizationPercent >= 100
                ? now
                : now + TimeSpan.FromHours((100 - window.UtilizationPercent) / percentPerHour.Value);
        }

        return new BlockAnalytics(
            start,
            end,
            elapsed,
            period.Tokens,
            period.Cost,
            tokensPerHour,
            percentPerHour,
            projected,
            projected is { } p && p < end);
    }
}
