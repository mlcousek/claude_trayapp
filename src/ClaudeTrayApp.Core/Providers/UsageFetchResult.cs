using ClaudeTrayApp.Core.Domain;

namespace ClaudeTrayApp.Core.Providers;

public enum UsageFetchStatus
{
    Success,
    NoCredentials,
    TokenExpired,
    Unauthenticated,
    RateLimited,
    ServerError,
    NetworkError,
    ParseError,
}

/// <summary>Outcome of one fetch. <see cref="Message"/> is user-facing and never contains a secret.</summary>
public sealed record UsageFetchResult(UsageFetchStatus Status, UsageSnapshot? Snapshot, TimeSpan? RetryAfter, string? Message)
{
    public bool IsSuccess => Status == UsageFetchStatus.Success;

    public static UsageFetchResult Success(UsageSnapshot snapshot) => new(UsageFetchStatus.Success, snapshot, null, null);

    public static UsageFetchResult Failed(UsageFetchStatus status, string message, TimeSpan? retryAfter = null) =>
        new(status, null, retryAfter, message);
}

/// <summary>Anything that can produce a usage snapshot. Knows about transport, never about UI.</summary>
public interface IUsageProvider
{
    string Name { get; }

    Task<UsageFetchResult> FetchAsync(CancellationToken cancellationToken);
}
