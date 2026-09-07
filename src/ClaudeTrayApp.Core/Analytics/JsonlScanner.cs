using System.Diagnostics;
using ClaudeTrayApp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Core.Analytics;

/// <summary>
/// Incremental scan of every session log under the Claude Code projects folder. Each file's byte offset, length
/// and mtime live in the store, so a rescan reads only appended bytes; a shrunken file is read again from the start.
/// Locked files and malformed lines are skipped, never fatal.
/// </summary>
public sealed class JsonlScanner
{
    private const int ChunkSize = 1024 * 1024;
    private readonly string _projectsDirectory;
    private readonly IAnalyticsStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<JsonlScanner> _logger;
    private readonly TimeSpan? _maxAge;

    public JsonlScanner(string projectsDirectory, IAnalyticsStore store, TimeProvider clock, ILogger<JsonlScanner> logger, TimeSpan? maxAge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsDirectory);
        _projectsDirectory = projectsDirectory;
        _store = store;
        _clock = clock;
        _logger = logger;
        _maxAge = maxAge;
    }

    public ScanResult Scan(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_projectsDirectory))
        {
            return ScanResult.Empty;
        }

        var stopwatch = Stopwatch.StartNew();
        var files = 0;
        var changed = 0;
        var inserted = 0;
        var skipped = 0;
        var cutoff = _maxAge is { } age ? _clock.GetUtcNow() - age : (DateTimeOffset?)null;

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(_projectsDirectory, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Session logs could not be listed ({Reason})", ex.GetType().Name);
            return ScanResult.Empty;
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files++;
            try
            {
                var outcome = ScanFile(path, cutoff);
                if (outcome is { } count)
                {
                    changed++;
                    inserted += count;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                _logger.LogDebug("Skipped {File}: {Reason}", Path.GetFileName(path), ex.GetType().Name);
            }
        }

        var result = new ScanResult(files, changed, inserted, skipped, stopwatch.Elapsed);
        _logger.LogInformation(
            "Session log scan: {Files} files, {Changed} changed, {Inserted} new events, {Skipped} skipped, {Elapsed} ms",
            files,
            changed,
            inserted,
            skipped,
            (long)stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <summary>Returns the number of new events, or null when the file was unchanged or too old to matter.</summary>
    private int? ScanFile(string path, DateTimeOffset? cutoff)
    {
        var info = new FileInfo(path);
        var state = _store.GetScanState(path);
        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;

        if (state is null && cutoff is { } limit && info.LastWriteTimeUtc < limit.UtcDateTime)
        {
            return null;
        }

        if (state is not null && state.Length == info.Length && state.LastWriteTicks == lastWriteTicks)
        {
            return null;
        }

        var offset = state?.Offset ?? 0;
        if (info.Length < offset)
        {
            offset = 0;
        }

        var events = new List<UsageEvent>();
        long consumed;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize))
        {
            stream.Seek(offset, SeekOrigin.Begin);
            consumed = ReadCompleteLines(stream, offset, events);
        }

        var inserted = events.Count > 0 ? _store.InsertEvents(events) : 0;
        _store.SetScanState(new ScanState(path, consumed, info.Length, lastWriteTicks));
        return inserted;
    }

    /// <summary>Reads whole lines from <paramref name="start"/>; a trailing partial line is left for the next scan.</summary>
    internal static long ReadCompleteLines(Stream stream, long start, List<UsageEvent> events)
    {
        var buffer = new byte[ChunkSize];
        var carry = Array.Empty<byte>();
        var position = start;
        var consumed = start;

        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            var data = carry.Length == 0 ? buffer.AsMemory(0, read) : Concat(carry, buffer.AsSpan(0, read));
            var lineStart = 0;
            var span = data.Span;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] != (byte)'\n')
                {
                    continue;
                }

                var length = i - lineStart;
                if (length > 0 && span[i - 1] == (byte)'\r')
                {
                    length--;
                }

                if (JsonlLineParser.TryParse(data.Slice(lineStart, length)) is { } usageEvent)
                {
                    events.Add(usageEvent);
                }

                lineStart = i + 1;
            }

            position += read;
            consumed = position - (data.Length - lineStart);
            carry = lineStart < data.Length ? data[lineStart..].ToArray() : [];
        }

        return consumed;
    }

    private static Memory<byte> Concat(byte[] carry, ReadOnlySpan<byte> next)
    {
        var joined = new byte[carry.Length + next.Length];
        carry.CopyTo(joined, 0);
        next.CopyTo(joined.AsSpan(carry.Length));
        return joined;
    }
}
