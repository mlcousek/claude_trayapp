using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeTrayApp.Core.ClaudeCode;
using ClaudeTrayApp.Core.Credentials;
using ClaudeTrayApp.Core.Diagnostics;
using ClaudeTrayApp.Core.Security;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Core.Providers;

/// <summary>
/// Fetches usage from the same undocumented endpoint Claude Code uses, authenticated with Claude Code's own token.
/// The token is attached per request and never logged; only HTTP status codes and a one-time response shape are.
/// </summary>
public sealed class OAuthUsageProvider : IUsageProvider
{
    public static readonly Uri DefaultEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    public const string BetaHeaderName = "anthropic-beta";
    public const string BetaHeaderValue = "oauth-2025-04-20";
    public const string UserAgentProduct = "claude-code";

    private readonly HttpClient _http;
    private readonly ICredentialSource _credentials;
    private readonly IClaudeCodeVersionDetector _versions;
    private readonly TimeProvider _clock;
    private readonly ILogger<OAuthUsageProvider> _logger;
    private readonly Uri _endpoint;
    private bool _shapeLogged;

    public OAuthUsageProvider(
        HttpClient http,
        ICredentialSource credentials,
        IClaudeCodeVersionDetector versions,
        TimeProvider clock,
        ILogger<OAuthUsageProvider> logger,
        Uri? endpoint = null)
    {
        _http = http;
        _credentials = credentials;
        _versions = versions;
        _clock = clock;
        _logger = logger;
        _endpoint = endpoint ?? DefaultEndpoint;
    }

    public string Name => "OAuth usage endpoint";

    /// <summary>When set, the next successful response logs its shape with short string values included (probe mode only).</summary>
    public bool VerboseShapeLogging { get; set; }

    public async Task<UsageFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        var lookup = await _credentials.ReadAsync(cancellationToken);
        if (lookup.Status != CredentialStatus.Found || lookup.Credentials is null)
        {
            return UsageFetchResult.Failed(UsageFetchStatus.NoCredentials, lookup.Detail ?? $"No Claude Code credentials ({_credentials.Description}).");
        }

        var credentials = lookup.Credentials;
        var now = _clock.GetUtcNow();
        if (credentials.IsExpired(now))
        {
            return UsageFetchResult.Failed(UsageFetchStatus.TokenExpired, "Claude Code sign-in has expired. Open Claude Code to refresh it.");
        }

        var version = await _versions.GetVersionAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation(BetaHeaderName, BetaHeaderValue);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProduct, version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Usage request failed: {Reason}", SecretRedactor.Redact(ex.Message, credentials.AccessToken));
            return UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "Could not reach api.anthropic.com. Check the network connection.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Usage request timed out after {Timeout}", _http.Timeout);
            return UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "The usage request timed out.");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.TooManyRequests:
                    var retryAfter = ReadRetryAfter(response.Headers.RetryAfter, now);
                    _logger.LogWarning("Usage endpoint rate-limited the request (429); Retry-After {RetryAfter}", retryAfter);
                    return UsageFetchResult.Failed(UsageFetchStatus.RateLimited, "Rate limited by the usage endpoint. Showing the last known values.", retryAfter);

                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    _logger.LogWarning("Usage endpoint rejected the token (HTTP {Status})", status);
                    return UsageFetchResult.Failed(UsageFetchStatus.Unauthenticated, "The usage endpoint rejected the sign-in. Open Claude Code to sign in again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Usage endpoint returned HTTP {Status}", status);
                return UsageFetchResult.Failed(UsageFetchStatus.ServerError, $"The usage endpoint returned HTTP {status}.");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                _logger.LogWarning("Usage response body could not be read: {Reason}", ex.GetType().Name);
                return UsageFetchResult.Failed(UsageFetchStatus.NetworkError, "The usage response was cut off.");
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                LogShapeOnce(document.RootElement);
                var snapshot = UsageResponseParser.Parse(document.RootElement, now, credentials.SubscriptionType);
                return UsageFetchResult.Success(snapshot);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("Usage response could not be parsed: {Reason}", SecretRedactor.Redact(ex.Message));
                return UsageFetchResult.Failed(UsageFetchStatus.ParseError, "The usage endpoint answered in an unexpected format.");
            }
        }
    }

    private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        return header.Date is { } date && date > now ? date - now : null;
    }

    private void LogShapeOnce(JsonElement root)
    {
        if (_shapeLogged || !_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _shapeLogged = true;
        var shape = JsonShapeDescriber.Describe(root, includeShortStrings: VerboseShapeLogging);
        _logger.LogDebug("Usage response shape: {Shape}", SecretRedactor.Redact(shape));
    }
}
