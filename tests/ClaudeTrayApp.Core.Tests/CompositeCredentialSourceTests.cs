using ClaudeTrayApp.Core.Credentials;
using ClaudeTrayApp.Core.Tests.TestSupport;
using Shouldly;

namespace ClaudeTrayApp.Core.Tests;

public class CompositeCredentialSourceTests
{
    [Fact]
    public async Task Returns_the_first_source_that_finds_credentials()
    {
        var composite = new CompositeCredentialSource([FakeCredentialSource.Missing(), FakeCredentialSource.Valid(), FakeCredentialSource.Expired()]);

        var lookup = await composite.ReadAsync(TestContext.Current.CancellationToken);

        lookup.Status.ShouldBe(CredentialStatus.Found);
        lookup.Credentials.ShouldNotBeNull().SubscriptionType.ShouldBe("max");
    }

    [Fact]
    public async Task Prefers_the_most_informative_failure()
    {
        var invalid = new FakeCredentialSource(CredentialLookup.Invalid("broken file"));
        var composite = new CompositeCredentialSource([FakeCredentialSource.Missing(), invalid, FakeCredentialSource.Missing()]);

        var lookup = await composite.ReadAsync(TestContext.Current.CancellationToken);

        lookup.Status.ShouldBe(CredentialStatus.Invalid);
        lookup.Detail.ShouldBe("broken file");
    }

    [Fact]
    public void Describes_every_source()
    {
        var composite = new CompositeCredentialSource([FakeCredentialSource.Missing(), FakeCredentialSource.Valid()]);

        composite.Description.ShouldBe("fake credential source, fake credential source");
    }

    [Fact]
    public void Requires_at_least_one_source() =>
        Should.Throw<ArgumentException>(() => new CompositeCredentialSource([]));
}
