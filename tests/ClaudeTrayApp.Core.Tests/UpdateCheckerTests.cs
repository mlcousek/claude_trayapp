using System.Net;
using ClaudeTrayApp.Core.Tests.TestSupport;
using ClaudeTrayApp.Core.Updates;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class UpdateCheckerTests
{
    private const string LatestBody = """
        {"tag_name":"v0.2.0","html_url":"https://github.com/mlcousek/claude_trayapp/releases/tag/v0.2.0"}
        """;

    [Theory]
    [InlineData("v0.1.1", "0.1.1")]
    [InlineData("0.1.1", "0.1.1")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("v10.0.1", "10.0.1")]
    public void Release_tags_parse_with_or_without_the_v(string tag, string expected) =>
        UpdateChecker.ParseVersion(tag).ShouldBe(Version.Parse(expected));

    [Theory]
    [InlineData("v0.2.0-beta.1")]
    [InlineData("0.2.0+build.5")]
    [InlineData("nightly")]
    [InlineData("")]
    [InlineData(null)]
    public void Prereleases_and_unreadable_tags_are_ignored(string? tag) =>
        UpdateChecker.ParseVersion(tag).ShouldBeNull();

    [Fact]
    public void A_two_part_version_never_looks_older_than_its_four_part_self()
    {
        // Version treats an unspecified revision as -1, so without normalising, 0.1.1 < 0.1.1.0 and every check
        // would claim an update was available.
        UpdateChecker.Normalize(new Version(0, 1, 1)).ShouldBe(UpdateChecker.Normalize(new Version(0, 1, 1, 0)));
    }

    [Fact]
    public async Task A_newer_release_is_reported_with_its_url()
    {
        var checker = Create("0.1.1", HttpStatusCode.OK, LatestBody);

        var outcome = await checker.CheckAsync(TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(UpdateCheckStatus.UpdateAvailable);
        outcome.Release.ShouldNotBeNull().Version.ShouldBe(new Version(0, 2, 0));
        outcome.Release!.Tag.ShouldBe("v0.2.0");
        outcome.Release.Url.ShouldBe("https://github.com/mlcousek/claude_trayapp/releases/tag/v0.2.0");
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.2.1")]
    [InlineData("1.0.0")]
    public async Task The_same_or_a_newer_build_is_up_to_date(string current)
    {
        var checker = Create(current, HttpStatusCode.OK, LatestBody);

        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.UpToDate);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_unhappy_response_fails_quietly(HttpStatusCode status)
    {
        var checker = Create("0.1.1", status, "{}");

        var outcome = await checker.CheckAsync(TestContext.Current.CancellationToken);

        outcome.Status.ShouldBe(UpdateCheckStatus.Failed);
        outcome.Release.ShouldBeNull();
        outcome.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_network_failure_never_escapes()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("no route to host"));
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.1", new ListLogger<UpdateChecker>());

        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.Failed);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"nightly\"}")]
    public async Task An_unreadable_body_fails_rather_than_inventing_a_version(string body)
    {
        var checker = Create("0.1.1", HttpStatusCode.OK, body);

        (await checker.CheckAsync(TestContext.Current.CancellationToken)).Status.ShouldBe(UpdateCheckStatus.Failed);
    }

    [Fact]
    public async Task The_request_identifies_the_app_and_asks_for_the_github_api()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(LatestBody) };
        });
        var checker = new UpdateChecker(new HttpClient(handler), "0.1.1", new ListLogger<UpdateChecker>());

        await checker.CheckAsync(TestContext.Current.CancellationToken);

        seen.ShouldNotBeNull();
        seen!.Method.ShouldBe(HttpMethod.Get);
        seen.RequestUri!.ToString().ShouldBe("https://api.github.com/repos/mlcousek/claude_trayapp/releases/latest");
        seen.Headers.UserAgent.ToString().ShouldStartWith("ClaudeUsageTray/");
        seen.Headers.Accept.ToString().ShouldContain("application/vnd.github+json");
        // Nothing about the user may ride along.
        seen.Content.ShouldBeNull();
        seen.Headers.Authorization.ShouldBeNull();
        seen.RequestUri.Query.ShouldBeEmpty();
    }

    private static UpdateChecker Create(string currentVersion, HttpStatusCode status, string body)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return new UpdateChecker(new HttpClient(handler), currentVersion, new ListLogger<UpdateChecker>());
    }
}
