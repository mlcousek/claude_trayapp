using System.Text.Json;

namespace ClaudeTrayApp.Core.Account;

/// <summary>
/// Reads the <c>oauthAccount</c> block of Claude Code's <c>.claude.json</c>. Nothing else in that file is read,
/// and the file is never written. Any problem yields <see cref="AccountInfo.Empty"/> rather than an error.
/// </summary>
public sealed class AccountInfoFileSource : IAccountInfoSource
{
    private const string AccountSection = "oauthAccount";
    private readonly string _filePath;

    public AccountInfoFileSource(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public async Task<AccountInfo> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return AccountInfo.Empty;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            return Parse(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AccountInfo.Empty;
        }
    }

    internal static AccountInfo Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(AccountSection, out var account)
                || account.ValueKind != JsonValueKind.Object)
            {
                return AccountInfo.Empty;
            }

            return new AccountInfo(
                ReadString(account, "emailAddress"),
                ReadString(account, "displayName"),
                ReadString(account, "organizationName"),
                ReadString(account, "billingType"),
                ReadString(account, "organizationRateLimitTier"),
                account.TryGetProperty("hasExtraUsageEnabled", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? flag.GetBoolean()
                    : null);
        }
        catch (JsonException)
        {
            return AccountInfo.Empty;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
