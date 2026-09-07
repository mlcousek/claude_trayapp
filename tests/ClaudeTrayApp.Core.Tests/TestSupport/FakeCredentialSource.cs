using ClaudeTrayApp.Core.ClaudeCode;
using ClaudeTrayApp.Core.Credentials;

namespace ClaudeTrayApp.Core.Tests.TestSupport;

internal sealed class FakeCredentialSource : ICredentialSource
{
    public const string Token = "FAKE-ACCESS-TOKEN-FOR-TESTS-0123456789";

    private readonly CredentialLookup _lookup;

    public FakeCredentialSource(CredentialLookup lookup)
    {
        _lookup = lookup;
    }

    public string Description => "fake credential source";

    public static FakeCredentialSource Valid(string? subscriptionType = "max") => new(CredentialLookup.Found(
        new ClaudeCredentials(Token, new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), subscriptionType, "default_claude_max_5x")));

    public static FakeCredentialSource Expired() => new(CredentialLookup.Found(
        new ClaudeCredentials(Token, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), "max", null)));

    public static FakeCredentialSource Missing() => new(CredentialLookup.NotFound("No credentials file (fake)."));

    public Task<CredentialLookup> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(_lookup);
}

internal sealed class FakeVersionDetector : IClaudeCodeVersionDetector
{
    private readonly string _version;

    public FakeVersionDetector(string version)
    {
        _version = version;
    }

    public ValueTask<string> GetVersionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_version);
}
