using System.Globalization;
using System.Text.Json;
using ClaudeTrayApp.Core.Domain;

namespace ClaudeTrayApp.Core.Credentials;

/// <summary>
/// Reads Claude Code's credentials file. Read-only: the file is never created, modified or refreshed by this app.
/// Only the <c>claudeAiOauth</c> section is parsed; unrelated sections such as <c>mcpOAuth</c> are never read.
/// </summary>
public sealed class CredentialFileSource : ICredentialSource
{
    private const string OAuthSection = "claudeAiOauth";
    private readonly string _filePath;

    public CredentialFileSource(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public string Description => $"credentials file {_filePath}";

    public async Task<CredentialLookup> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return CredentialLookup.NotFound($"No credentials file at {_filePath}. Sign in with Claude Code first.");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CredentialLookup.Unreadable($"Credentials file could not be read ({ex.GetType().Name}); it may be locked. Retrying later.");
        }

        return Parse(bytes);
    }

    /// <summary>
    /// Parses file content. Accepts the current layout (an object under <c>claudeAiOauth</c>)
    /// and a flat layout with the same keys at the root.
    /// </summary>
    internal static CredentialLookup Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CredentialLookup.Invalid("Credentials file is not a JSON object.");
            }

            var section = root.TryGetProperty(OAuthSection, out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested
                : root;

            if (!section.TryGetProperty("accessToken", out var token)
                || token.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token.GetString()))
            {
                return CredentialLookup.Invalid("Credentials file has no Claude access token. Sign in with Claude Code.");
            }

            return CredentialLookup.Found(new ClaudeCredentials(
                token.GetString()!,
                ReadExpiry(section),
                ReadString(section, "subscriptionType"),
                ReadString(section, "rateLimitTier")));
        }
        catch (JsonException)
        {
            return CredentialLookup.Invalid("Credentials file is not valid JSON; it may be mid-write. Retrying later.");
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? ReadExpiry(JsonElement element)
    {
        if (!element.TryGetProperty("expiresAt", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var epoch))
            {
                return EpochTime.FromUnixSecondsOrMilliseconds(epoch);
            }

            if (value.TryGetDouble(out var fractional) && double.IsFinite(fractional))
            {
                return EpochTime.FromUnixSecondsOrMilliseconds((long)fractional);
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
