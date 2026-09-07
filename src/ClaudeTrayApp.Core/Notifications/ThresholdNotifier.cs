using ClaudeTrayApp.Core.Domain;

namespace ClaudeTrayApp.Core.Notifications;

/// <summary>One crossing worth telling the user about: the highest threshold the window has passed this period.</summary>
public sealed record ThresholdAlert(string WindowKey, string WindowName, int Threshold, double Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// Decides when a threshold notification is due. Every threshold fires at most once per window per period, where a
/// period is identified by the window's reset time; when several thresholds are crossed at once only the highest is
/// reported. Pure and single-threaded by design; the caller serialises calls.
/// </summary>
public sealed class ThresholdNotifier
{
    private readonly Dictionary<string, (long Period, HashSet<int> Fired)> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alerts for this snapshot, given the enabled thresholds. An empty threshold list never alerts.</summary>
    public IReadOnlyList<ThresholdAlert> Evaluate(UsageSnapshot snapshot, IReadOnlyList<int> thresholds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);

        var alerts = new List<ThresholdAlert>();
        foreach (var window in snapshot.Windows)
        {
            var period = window.ResetsAt?.UtcTicks ?? 0;
            if (!_state.TryGetValue(window.Key, out var entry) || entry.Period != period)
            {
                entry = (period, []);
                _state[window.Key] = entry;
            }

            var crossed = thresholds.Where(t => window.UtilizationPercent >= t && entry.Fired.Add(t)).ToList();
            if (crossed.Count > 0)
            {
                alerts.Add(new ThresholdAlert(window.Key, window.DisplayName, crossed.Max(), window.UtilizationPercent, window.ResetsAt));
            }
        }

        return alerts;
    }

    /// <summary>Forgets everything, so the next snapshot can alert again.</summary>
    public void Reset() => _state.Clear();
}
