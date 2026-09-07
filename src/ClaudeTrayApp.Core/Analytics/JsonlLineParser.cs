using System.Globalization;
using System.Text.Json;

namespace ClaudeTrayApp.Core.Analytics;

/// <summary>
/// Turns one line of a Claude Code session log into a <see cref="UsageEvent"/>. Only assistant records carry usage;
/// everything else, including malformed or half-written lines, yields null and is skipped.
/// </summary>
public static class JsonlLineParser
{
    private static readonly byte[] AssistantMarker = "\"assistant\""u8.ToArray();

    /// <summary>Cheap pre-check so the JSON parser only runs on candidate lines.</summary>
    public static bool MightBeAssistant(ReadOnlySpan<byte> line) => line.IndexOf(AssistantMarker) >= 0;

    public static UsageEvent? TryParse(ReadOnlyMemory<byte> line)
    {
        if (line.IsEmpty || !MightBeAssistant(line.Span))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !string.Equals(ReadString(root, "type"), "assistant", StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var messageId = ReadString(message, "id") ?? ReadString(root, "uuid");
            var timestamp = ReadTimestamp(root);
            if (messageId is null || timestamp is null)
            {
                return null;
            }

            var cacheWrite5m = ReadLong(usage, "cache_creation_input_tokens");
            var cacheWrite1h = 0L;
            if (usage.TryGetProperty("cache_creation", out var split) && split.ValueKind == JsonValueKind.Object)
            {
                cacheWrite5m = ReadLong(split, "ephemeral_5m_input_tokens");
                cacheWrite1h = ReadLong(split, "ephemeral_1h_input_tokens");
            }

            return new UsageEvent(
                messageId,
                ReadString(root, "requestId") ?? string.Empty,
                timestamp.Value,
                ReadString(message, "model") ?? "unknown",
                ReadString(root, "cwd"),
                ReadString(root, "sessionId"),
                ReadLong(usage, "input_tokens"),
                ReadLong(usage, "output_tokens"),
                cacheWrite5m,
                cacheWrite1h,
                ReadLong(usage, "cache_read_input_tokens"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0
            ? number
            : 0;

    private static DateTimeOffset? ReadTimestamp(JsonElement root) =>
        ReadString(root, "timestamp") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
