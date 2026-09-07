using System.Globalization;
using System.Text.Json;
using ClaudeTrayApp.Core.Domain;

namespace ClaudeTrayApp.Core.Providers;

/// <summary>
/// Turns the usage endpoint's JSON into a <see cref="UsageSnapshot"/>. Defensive by design: every object
/// property that carries a utilisation number becomes a window, whatever its key; nulls and everything else are ignored.
/// </summary>
public static class UsageResponseParser
{
    private const string OverageKey = "extra_usage";
    private const string SpendKey = "spend";

    /// <summary>Top-level objects that are not rate-limit windows even though they carry percentages.</summary>
    private static readonly string[] NonWindowKeys = [OverageKey, SpendKey, "limits"];
    private static readonly string[] WindowPercentKeys = ["utilization", "utilization_percent", "used_percent", "percentage"];
    private static readonly string[] OveragePercentKeys = ["utilization", "percent", "utilization_percent"];
    private static readonly string[] ResetKeys = ["resets_at", "reset_at", "resets", "reset_time"];
    private static readonly string[] LockedKeys = ["locked_reason", "lock_reason"];
    private static readonly string[] PlanKeys = ["plan", "plan_tier", "subscription_type", "tier"];
    private static readonly string[] UsedKeys = ["used_credits", "used", "used_amount", "spent"];
    private static readonly string[] LimitKeys = ["monthly_limit", "limit", "limit_amount", "budget"];
    private static readonly string[] EnabledKeys = ["is_enabled", "enabled"];
    private static readonly string[] CurrencyKeys = ["currency"];

    public static UsageSnapshot Parse(JsonElement root, DateTimeOffset now, string? planTierFallback = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Usage response is not a JSON object.");
        }

        var windows = new List<UsageWindow>();
        JsonElement? extraUsage = null;
        JsonElement? spend = null;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (property.NameEquals(OverageKey))
            {
                extraUsage = property.Value;
                continue;
            }

            if (property.NameEquals(SpendKey))
            {
                spend = property.Value;
                continue;
            }

            if (IsNonWindow(property.Name))
            {
                continue;
            }

            if (TryReadNumber(property.Value, WindowPercentKeys, out var percent))
            {
                windows.Add(UsageWindow.Create(
                    property.Name,
                    percent,
                    ReadReset(property.Value),
                    lockedReason: ReadString(property.Value, LockedKeys)));
            }
        }

        windows.Sort(CompareWindows);
        var plan = ReadString(root, PlanKeys) ?? planTierFallback;
        return new UsageSnapshot(windows, plan, null, ParseOverage(extraUsage, spend), now, UsageSource.Live);
    }

    private static bool IsNonWindow(string name) =>
        Array.Exists(NonWindowKeys, key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    private static int CompareWindows(UsageWindow left, UsageWindow right)
    {
        var leftIndex = PreferredIndex(left.Key);
        var rightIndex = PreferredIndex(right.Key);
        return leftIndex != rightIndex ? leftIndex.CompareTo(rightIndex) : string.CompareOrdinal(left.Key, right.Key);

        static int PreferredIndex(string key)
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

    /// <summary>
    /// Extra usage comes from two blocks that describe the same money: <c>extra_usage</c> (amounts in minor units,
    /// scaled by <c>decimal_places</c>) and <c>spend</c> (<c>amount_minor</c> plus <c>exponent</c>). The first is
    /// authoritative; the second fills whatever it leaves null.
    /// </summary>
    private static OverageInfo? ParseOverage(JsonElement? extraUsage, JsonElement? spend)
    {
        if (extraUsage is null && spend is null)
        {
            return null;
        }

        bool? enabled = null;
        decimal? used = null;
        decimal? limit = null;
        double? utilization = null;
        string? currency = null;

        if (extraUsage is { } extra)
        {
            enabled = ReadBool(extra, EnabledKeys);
            var decimals = ReadInt(extra, "decimal_places");
            used = Scale(ReadDecimal(extra, UsedKeys), decimals);
            limit = Scale(ReadDecimal(extra, LimitKeys), decimals);
            utilization = TryReadNumber(extra, OveragePercentKeys, out var percent) ? percent : null;
            currency = ReadString(extra, CurrencyKeys);
        }

        if (spend is { } spent)
        {
            enabled ??= ReadBool(spent, EnabledKeys);
            used ??= ReadMoney(spent, "used");
            limit ??= ReadMoney(spent, "limit");
            utilization ??= TryReadNumber(spent, OveragePercentKeys, out var percent) ? percent : null;
            currency ??= spent.TryGetProperty("used", out var money) && money.ValueKind == JsonValueKind.Object
                ? ReadString(money, CurrencyKeys)
                : null;
        }

        utilization ??= used is { } u && limit is > 0 ? (double)(u / limit.Value * 100) : null;
        return new OverageInfo(enabled ?? false, used, limit, utilization, currency);
    }

    private static decimal? ReadMoney(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var money) || money.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return money.TryGetProperty("amount_minor", out var minor) && minor.ValueKind == JsonValueKind.Number && minor.TryGetDecimal(out var amount)
            ? Scale(amount, ReadInt(money, "exponent") ?? 2)
            : null;
    }

    private static decimal? Scale(decimal? amount, int? decimals)
    {
        if (amount is null || decimals is null or <= 0)
        {
            return amount;
        }

        var divisor = 1m;
        for (var i = 0; i < Math.Min(decimals.Value, 12); i++)
        {
            divisor *= 10;
        }

        return amount / divisor;
    }

    private static bool? ReadBool(JsonElement element, string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return flag.GetBoolean();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static DateTimeOffset? ReadReset(JsonElement element)
    {
        foreach (var key in ResetKeys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
            {
                return EpochTime.FromUnixSecondsOrMilliseconds(epoch);
            }
        }

        return null;
    }

    private static bool TryReadNumber(JsonElement element, string[] keys, out double number)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return true;
            }
        }

        number = 0;
        return false;
    }

    private static decimal? ReadDecimal(JsonElement element, string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }
}
