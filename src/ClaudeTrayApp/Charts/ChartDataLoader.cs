using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Domain;
using ClaudeTrayApp.Core.Storage;

namespace ClaudeTrayApp.Charts;

/// <summary>
/// Runs the history and daily queries for one chart refresh. Meant for a thread-pool thread: the store opens a
/// connection per call, and nothing here touches WPF.
/// </summary>
public sealed class ChartDataLoader
{
    private readonly IHistoryStore _history;
    private readonly AnalyticsCalculator _calculator;
    private readonly TimeZoneInfo _zone;

    public ChartDataLoader(IHistoryStore history, AnalyticsCalculator calculator, TimeZoneInfo? zone = null)
    {
        _history = history;
        _calculator = calculator;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public ChartBundle Load(UsageSnapshot? snapshot, BlockAnalytics? block, int rangeHours, DateTimeOffset now)
    {
        var from = now - TimeSpan.FromHours(Math.Max(1, rangeHours));
        var windows = OrderKeys(_history.GetWindowKeys())
            .Select(key => (key, WindowNameHumanizer.Humanize(key), _history.GetSeries(key, from, now)))
            .ToList();
        var history = ChartDataBuilder.History(windows, from, now);

        BurnChart? burn = null;
        if (block is not null)
        {
            var fiveHour = _history.GetSeries(WindowKeys.FiveHour, block.Start, now);
            burn = ChartDataBuilder.Burn(fiveHour, block, snapshot?.FindWindow(WindowKeys.FiveHour)?.UtilizationPercent, now, _zone);
        }

        var localNow = TimeZoneInfo.ConvertTime(now, _zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var firstDay = new DateTimeOffset(localNow.Date, localNow.Offset).AddDays(-(ChartDataBuilder.DailyDays - 1));
        var daily = ChartDataBuilder.Daily(_calculator.Daily(firstDay, now), today);

        var sparklines = new Dictionary<string, SparkData>(StringComparer.OrdinalIgnoreCase);
        foreach (var window in snapshot?.Windows ?? [])
        {
            var (since, until) = SparkRange(window.Key, block, now);
            var points = burn is not null && string.Equals(window.Key, WindowKeys.FiveHour, StringComparison.OrdinalIgnoreCase)
                ? burn.Recorded
                : ChartDataBuilder.Sparkline(_history.GetSeries(window.Key, since, until), since, until);
            sparklines[window.Key] = new SparkData(since, until, points);
        }

        return new ChartBundle(history, burn, daily, sparklines);
    }

    /// <summary>Known windows in their preferred order, then unknown keys alphabetically.</summary>
    internal static IReadOnlyList<string> OrderKeys(IEnumerable<string> keys) =>
        keys.OrderBy(Rank).ThenBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>The 5-hour sparkline spans its block; weekly windows span a week; anything else the last day.</summary>
    internal static (DateTimeOffset Since, DateTimeOffset Until) SparkRange(string key, BlockAnalytics? block, DateTimeOffset now)
    {
        if (string.Equals(key, WindowKeys.FiveHour, StringComparison.OrdinalIgnoreCase))
        {
            return block is not null ? (block.Start, block.End) : (now - AnalyticsCalculator.BlockLength, now);
        }

        return key.StartsWith("seven_day", StringComparison.OrdinalIgnoreCase)
            ? (now - TimeSpan.FromDays(7), now)
            : (now - TimeSpan.FromHours(24), now);
    }

    private static int Rank(string key)
    {
        for (var i = 0; i < WindowKeys.PreferredOrder.Count; i++)
        {
            if (string.Equals(WindowKeys.PreferredOrder[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
