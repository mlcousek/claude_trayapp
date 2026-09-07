using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClaudeTrayApp.Core.Credentials;
using ClaudeTrayApp.Core.Providers;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class OAuthUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    private static string Typical() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "usage", "typical.json"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (OAuthUsageProvider Provider, StubHttpMessageHandler Handler, ListLogger<OAuthUsageProvider> Log) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        ICredentialSource? credentials = null)
    {
        var handler = new StubHttpMessageHandler(respond);
        var log = new ListLogger<OAuthUsageProvider>();
        var provider = new OAuthUsageProvider(
            new HttpClient(handler),
            credentials ?? FakeCredentialSource.Valid(),
            new FakeVersionDetector("1.2.3"),
            new FakeTimeProvider(Now),
            log);
        return (provider, handler, log);
    }

    [Fact]
    public async Task Sends_the_headers_the_endpoint_expects()
    {
        var (provider, handler, _) = Create(_ => Json(HttpStatusCode.OK, Typical()));

        await provider.FetchAsync(TestContext.Current.CancellationToken);

        var request = handler.LastRequest.ShouldNotBeNull();
        request.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri.ShouldBe(OAuthUsageProvider.DefaultEndpoint);
        request.Headers.Authorization.ShouldBe(new AuthenticationHeaderValue("Bearer", FakeCredentialSource.Token));
        request.Headers.GetValues("anthropic-beta").ShouldBe(["oauth-2025-04-20"]);
        request.Headers.UserAgent.ToString().ShouldBe("claude-code/1.2.3");
        request.Headers.Accept.ToString().ShouldBe("application/json");
    }

    [Fact]
    public async Task Parses_a_successful_response_and_falls_back_to_the_credential_plan()
    {
        var (provider, _, log) = Create(_ => Json(HttpStatusCode.OK, Typical()));

        var result = await provider.FetchAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UsageFetchStatus.Success);
        var snapshot = result.Snapshot.ShouldNotBeNull();
        snapshot.Windows.Count.ShouldBe(4);
        snapshot.PlanTier.ShouldBe("max");
        snapshot.LastUpdated.ShouldBe(Now);
        log.All.ShouldContain("Usage response shape");
        log.All.ShouldNotContain(FakeCredentialSource.Token);
    }

    [Fact]
    public async Task Logs_the_response_shape_only_once()
    {
        var (provider, _, log) = Create(_ => Json(HttpStatusCode.OK, Typical()));

        await provider.FetchAsync(TestContext.Current.CancellationToken);
        await provider.FetchAsync(TestContext.Current.CancellationToken);

        log.Entries.Count(e => e.Contains("Usage response shape", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Maps_429_to_rate_limited_with_retry_after()
    {
        var (provider, _, _) = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var result = await provider.FetchAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UsageFetchStatus.RateLimited);
        result.RetryAfter.ShouldBe(TimeSpan.FromSeconds(120));
        result.Snapshot.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UsageFetchStatus.Unauthenticated)]
    [InlineData(HttpStatusCode.Forbidden, UsageFetchStatus.Unauthenticated)]
    [InlineData(HttpStatusCode.InternalServerError, UsageFetchStatus.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, UsageFetchStatus.ServerError)]
    [InlineData(HttpStatusCode.NotFound, UsageFetchStatus.ServerError)]
    public async Task Maps_http_failures(HttpStatusCode code, UsageFetchStatus expected)
    {
        var (provider, _, _) = Create(_ => new HttpResponseMessage(code));

        var result = await provider.FetchAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(expected);
        result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Maps_network_errors_and_redacts_the_token_from_logs()
    {
        var (provider, _, log) = Create(_ => throw new HttpRequestException("connection reset while sending " + FakeCredentialSource.Token));

        var result = await provider.FetchAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UsageFetchStatus.NetworkError);
        log.All.ShouldContain("connection reset");
        log.All.ShouldNotContain(FakeCredentialSource.Token);
    }

    [Fact]
    public async Task Maps_an_unparseable_body_to_parse_error()
    {
        var (provider, _, _) = Create(_ => Json(HttpStatusCode.OK, "<html>not json</html>"));

        var result = await provider.FetchAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UsageFetchStatus.ParseError);
    }

    [Fact]
    public async Task Does_not_call_the_endpoint_without_credentials()
    {
        var (provider, handler, _) = Create(_ => Json(HttpStatusCode.OK, Typical()), FakeCredentialSource.Missing());

        var result = await provider.FetchAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UsageFetchStatus.NoCredentials);
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Does_not_call_the_endpoint_with_an_expired_token()
    {
        var (provider, handler, _) = Create(_ => Json(HttpStatusCode.OK, Typical()), FakeCredentialSource.Expired());

        var result = await provider.FetchAsync(TestContext.Current.CancellationToken);

        result.Status.ShouldBe(UsageFetchStatus.TokenExpired);
        result.Message.ShouldNotBeNull().ShouldContain("Open Claude Code");
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Never_logs_the_token_in_any_scenario()
    {
        var scenarios = new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => Json(HttpStatusCode.OK, Typical()),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => Json(HttpStatusCode.OK, "nope"),
            _ => throw new HttpRequestException(FakeCredentialSource.Token),
        };

        foreach (var scenario in scenarios)
        {
            var (provider, _, log) = Create(scenario);
            await provider.FetchAsync(TestContext.Current.CancellationToken);
            log.All.ShouldNotContain(FakeCredentialSource.Token);
        }
    }
}
