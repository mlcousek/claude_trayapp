using System.Globalization;

namespace ClaudeTrayApp.Core.Credentials;

public enum CredentialStatus
{
    Found,
    NotFound,
    Unreadable,
    Invalid,
}

/// <summary>
/// The subset of Claude Code's OAuth credentials this app needs.
/// <see cref="ToString"/> is overridden so an accidental log call can never print the token.
/// </summary>
public sealed record ClaudeCredentials(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    string? RateLimitTier)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"ClaudeCredentials(AccessToken=[redacted], ExpiresAt={ExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? "null"}, SubscriptionType={SubscriptionType}, RateLimitTier={RateLimitTier})");
}

/// <summary>Outcome of a credential lookup. <see cref="Detail"/> is safe to show to the user; it never contains a secret.</summary>
public sealed record CredentialLookup(CredentialStatus Status, ClaudeCredentials? Credentials, string? Detail)
{
    public static CredentialLookup Found(ClaudeCredentials credentials) => new(CredentialStatus.Found, credentials, null);

    public static CredentialLookup NotFound(string detail) => new(CredentialStatus.NotFound, null, detail);

    public static CredentialLookup Unreadable(string detail) => new(CredentialStatus.Unreadable, null, detail);

    public static CredentialLookup Invalid(string detail) => new(CredentialStatus.Invalid, null, detail);
}

/// <summary>A place credentials can come from. Implementations are read-only by contract.</summary>
public interface ICredentialSource
{
    /// <summary>Human-readable description for UI and logs, such as the file path. Never contains a secret.</summary>
    string Description { get; }

    Task<CredentialLookup> ReadAsync(CancellationToken cancellationToken);
}
