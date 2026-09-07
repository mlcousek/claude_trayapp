using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeTrayApp.Core.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Core.Cache;

public interface ISnapshotCache
{
    Task<UsageSnapshot?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(UsageSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>
/// Persists the last snapshot to <c>cache.json</c> so the app shows data immediately on launch.
/// The file holds percentages and timestamps only; it never contains a token.
/// </summary>
public sealed class SnapshotCache : ISnapshotCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly ILogger<SnapshotCache> _logger;

    public SnapshotCache(string filePath, ILogger<SnapshotCache> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<UsageSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(_filePath);
            await using (stream.ConfigureAwait(false))
            {
                var snapshot = await JsonSerializer.DeserializeAsync<UsageSnapshot>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                return snapshot is null ? null : snapshot with { Source = UsageSource.Cache };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cached snapshot could not be read ({Reason}); starting without it", ex.GetType().Name);
            return null;
        }
    }

    public async Task SaveAsync(UsageSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var temporary = _filePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = File.Create(temporary);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Snapshot cache could not be written ({Reason})", ex.GetType().Name);
        }
    }
}
