namespace ClaudeTrayApp.Core.Credentials;

/// <summary>
/// Tries several sources in order and returns the first hit. When nothing is found it reports the most
/// informative failure, so "invalid file" wins over "no file" in the message shown to the user.
/// </summary>
public sealed class CompositeCredentialSource : ICredentialSource
{
    private readonly List<ICredentialSource> _sources;

    public CompositeCredentialSource(IEnumerable<ICredentialSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToList();
        if (_sources.Count == 0)
        {
            throw new ArgumentException("At least one credential source is required.", nameof(sources));
        }
    }

    public string Description => string.Join(", ", _sources.Select(s => s.Description));

    public async Task<CredentialLookup> ReadAsync(CancellationToken cancellationToken)
    {
        CredentialLookup? best = null;
        foreach (var source in _sources)
        {
            var lookup = await source.ReadAsync(cancellationToken);
            if (lookup.Status == CredentialStatus.Found)
            {
                return lookup;
            }

            if (best is null || Rank(lookup.Status) > Rank(best.Status))
            {
                best = lookup;
            }
        }

        return best!;
    }

    private static int Rank(CredentialStatus status) => status switch
    {
        CredentialStatus.Invalid => 3,
        CredentialStatus.Unreadable => 2,
        CredentialStatus.NotFound => 1,
        _ => 0,
    };
}
