using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeTrayApp.Core.Domain;

/// <summary>
/// Turns an endpoint window key such as <c>seven_day_opus</c> into "7-day Opus".
/// Unknown keys still get a readable name so a schema change never hides a window.
/// </summary>
public static partial class WindowNameHumanizer
{
    private static readonly char[] Separators = ['_', '-', ' ', '.'];

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [WindowKeys.FiveHour] = "5-hour",
        [WindowKeys.SevenDay] = "7-day",
        [WindowKeys.SevenDayOpus] = "7-day Opus",
        [WindowKeys.SevenDaySonnet] = "7-day Sonnet",
        [WindowKeys.SevenDayOAuthApps] = "7-day OAuth apps",
    };

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["twelve"] = 12,
        ["fourteen"] = 14,
        ["thirty"] = 30,
        ["sixty"] = 60,
        ["ninety"] = 90,
    };

    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        "minute", "hour", "day", "week", "month", "year",
    };

    private static readonly Dictionary<string, string> SpecialCasing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opus"] = "Opus",
        ["sonnet"] = "Sonnet",
        ["haiku"] = "Haiku",
        ["oauth"] = "OAuth",
        ["api"] = "API",
        ["ai"] = "AI",
        ["mcp"] = "MCP",
    };

    public static string Humanize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Unknown";
        }

        if (KnownNames.TryGetValue(key, out var known))
        {
            return known;
        }

        var parts = CamelBoundary().Replace(key, "_")
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var words = new List<string>(parts.Length);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i + 1 < parts.Length && Units.Contains(parts[i + 1]) && TryNumber(part, out var number))
            {
                words.Add(string.Create(CultureInfo.InvariantCulture, $"{number}-{Lower(parts[i + 1])}"));
                i++;
                continue;
            }

            if (SpecialCasing.TryGetValue(part, out var special))
            {
                words.Add(special);
            }
            else
            {
                words.Add(words.Count == 0 ? Capitalize(part) : Lower(part));
            }
        }

        return words.Count == 0 ? key : string.Join(' ', words);
    }

    private static bool TryNumber(string text, out int number) =>
        NumberWords.TryGetValue(text, out number)
        || int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static string Lower(string text) => text.ToLower(CultureInfo.InvariantCulture);

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + Lower(text[1..]);

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundary();
}
