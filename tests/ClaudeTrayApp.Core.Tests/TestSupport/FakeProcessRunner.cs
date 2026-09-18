using ClaudeTrayApp.Core.ClaudeCode;
using ClaudeTrayApp.Core.Credentials;

namespace ClaudeTrayApp.Core.Tests.TestSupport;

/// <summary>Records every run and answers with a scripted result; <see cref="OnRun"/> can mutate test state, such as a credential file.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<(ProcessRunRequest Request, TimeSpan Timeout)> Runs { get; } = [];

    public ProcessRunResult Result { get; set; } = new(0, false, TimeSpan.FromMilliseconds(2500), null);

    public Action<ProcessRunRequest>? OnRun { get; set; }

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Runs.Add((request, timeout));
        OnRun?.Invoke(request);
        return Task.FromResult(Result);
    }
}

/// <summary>Answers each read with the next scripted lookup, repeating the last one.</summary>
internal sealed class ScriptedCredentialSource : ICredentialSource
{
    private readonly Queue<CredentialLookup> _lookups;
    private CredentialLookup _last;

    public ScriptedCredentialSource(params CredentialLookup[] lookups)
    {
        _lookups = new Queue<CredentialLookup>(lookups);
        _last = lookups[^1];
    }

    public int Reads { get; private set; }

    public string Description => "scripted credential source";

    public static CredentialLookup ExpiredAt(DateTimeOffset expiry) =>
        CredentialLookup.Found(new ClaudeCredentials(FakeCredentialSource.Token, expiry, "max", null));

    public Task<CredentialLookup> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++;
        if (_lookups.Count > 0)
        {
            _last = _lookups.Dequeue();
        }

        return Task.FromResult(_last);
    }
}

internal sealed class FakeCliLocator : IClaudeCliLocator
{
    private readonly ClaudeCliLocation? _location;

    public FakeCliLocator(ClaudeCliLocation? location)
    {
        _location = location;
    }

    public int Calls { get; private set; }

    public static FakeCliLocator Native() => new(new ClaudeCliLocation(Path.Combine("C:", "tools", "claude.exe"), ClaudeCliKind.Executable, ClaudeCliLocator.PathSource));

    public static FakeCliLocator Missing() => new(null);

    public ClaudeCliLocation? Locate()
    {
        Calls++;
        return _location;
    }
}

internal sealed class FakeRefreshNudge : ICredentialRefreshNudge
{
    private readonly CredentialRefreshOutcome _outcome;

    public FakeRefreshNudge(CredentialRefreshOutcome outcome)
    {
        _outcome = outcome;
    }

    public int Calls { get; private set; }

    public Task<CredentialRefreshOutcome> TryRefreshAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_outcome);
    }
}
