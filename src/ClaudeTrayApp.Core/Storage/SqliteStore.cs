using ClaudeTrayApp.Core.Analytics;
using ClaudeTrayApp.Core.Domain;
using Microsoft.Data.Sqlite;

namespace ClaudeTrayApp.Core.Storage;

/// <summary>
/// One SQLite file (<c>history.db</c>) for usage events, scan offsets and snapshot history.
/// Timestamps are stored as UTC ticks so range queries are plain integer comparisons.
/// </summary>
public sealed class SqliteStore : IAnalyticsStore, IHistoryStore
{
    private readonly string _connectionString;
    private readonly object _sync = new();
    private bool _initialised;

    public SqliteStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public string DatabasePath { get; }

    public ScanState? GetScanState(string path)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT offset, length, mtime_ticks FROM scan_state WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ScanState(path, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    public void SetScanState(ScanState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_state (path, offset, length, mtime_ticks) VALUES ($path, $offset, $length, $mtime)
            ON CONFLICT(path) DO UPDATE SET offset = excluded.offset, length = excluded.length, mtime_ticks = excluded.mtime_ticks
            """;
        command.Parameters.AddWithValue("$path", state.Path);
        command.Parameters.AddWithValue("$offset", state.Offset);
        command.Parameters.AddWithValue("$length", state.Length);
        command.Parameters.AddWithValue("$mtime", state.LastWriteTicks);
        command.ExecuteNonQuery();
    }

    public int InsertEvents(IReadOnlyCollection<UsageEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return 0;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO usage_events
                (message_id, request_id, timestamp_ticks, model, project, session_id, input, output, cache_write_5m, cache_write_1h, cache_read)
            VALUES ($message, $request, $ticks, $model, $project, $session, $input, $output, $cw5m, $cw1h, $read)
            """;
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var request = command.Parameters.Add("$request", SqliteType.Text);
        var ticks = command.Parameters.Add("$ticks", SqliteType.Integer);
        var model = command.Parameters.Add("$model", SqliteType.Text);
        var project = command.Parameters.Add("$project", SqliteType.Text);
        var session = command.Parameters.Add("$session", SqliteType.Text);
        var input = command.Parameters.Add("$input", SqliteType.Integer);
        var output = command.Parameters.Add("$output", SqliteType.Integer);
        var cw5m = command.Parameters.Add("$cw5m", SqliteType.Integer);
        var cw1h = command.Parameters.Add("$cw1h", SqliteType.Integer);
        var read = command.Parameters.Add("$read", SqliteType.Integer);

        var inserted = 0;
        foreach (var usageEvent in events)
        {
            message.Value = usageEvent.MessageId;
            request.Value = usageEvent.RequestId;
            ticks.Value = usageEvent.Timestamp.UtcTicks;
            model.Value = usageEvent.Model;
            project.Value = (object?)usageEvent.Project ?? DBNull.Value;
            session.Value = (object?)usageEvent.SessionId ?? DBNull.Value;
            input.Value = usageEvent.InputTokens;
            output.Value = usageEvent.OutputTokens;
            cw5m.Value = usageEvent.CacheWrite5mTokens;
            cw1h.Value = usageEvent.CacheWrite1hTokens;
            read.Value = usageEvent.CacheReadTokens;
            inserted += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return inserted;
    }

    public long CountEvents()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_events";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<ModelTotals> TotalsByModel(DateTimeOffset since, DateTimeOffset until)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT model, SUM(input), SUM(output), SUM(cache_write_5m), SUM(cache_write_1h), SUM(cache_read), COUNT(*)
            FROM usage_events WHERE timestamp_ticks >= $from AND timestamp_ticks < $to
            GROUP BY model ORDER BY SUM(input + output + cache_write_5m + cache_write_1h + cache_read) DESC
            """;
        command.Parameters.AddWithValue("$from", since.UtcTicks);
        command.Parameters.AddWithValue("$to", until.UtcTicks);
        using var reader = command.ExecuteReader();
        var result = new List<ModelTotals>();
        while (reader.Read())
        {
            result.Add(new ModelTotals(reader.GetString(0), ReadTotals(reader, 1)));
        }

        return result;
    }

    public IReadOnlyList<ProjectUsage> TopProjects(DateTimeOffset since, DateTimeOffset until, int count)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(project, ''), SUM(input + output + cache_write_5m + cache_write_1h + cache_read) AS tokens
            FROM usage_events WHERE timestamp_ticks >= $from AND timestamp_ticks < $to
            GROUP BY project ORDER BY tokens DESC LIMIT $count
            """;
        command.Parameters.AddWithValue("$from", since.UtcTicks);
        command.Parameters.AddWithValue("$to", until.UtcTicks);
        command.Parameters.AddWithValue("$count", count);
        using var reader = command.ExecuteReader();
        var result = new List<ProjectUsage>();
        while (reader.Read())
        {
            result.Add(new ProjectUsage(reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    public IReadOnlyList<DailyModelTotals> DailyTotals(DateTimeOffset since, DateTimeOffset until, TimeSpan localOffset)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (timestamp_ticks + $offset) / $day AS day, model,
                   SUM(input), SUM(output), SUM(cache_write_5m), SUM(cache_write_1h), SUM(cache_read), COUNT(*)
            FROM usage_events WHERE timestamp_ticks >= $from AND timestamp_ticks < $to
            GROUP BY day, model ORDER BY day, model
            """;
        command.Parameters.AddWithValue("$offset", localOffset.Ticks);
        command.Parameters.AddWithValue("$day", TimeSpan.TicksPerDay);
        command.Parameters.AddWithValue("$from", since.UtcTicks);
        command.Parameters.AddWithValue("$to", until.UtcTicks);
        using var reader = command.ExecuteReader();
        var result = new List<DailyModelTotals>();
        while (reader.Read())
        {
            var day = DateOnly.FromDateTime(new DateTime(reader.GetInt64(0) * TimeSpan.TicksPerDay, DateTimeKind.Unspecified));
            result.Add(new DailyModelTotals(day, reader.GetString(1), ReadTotals(reader, 2)));
        }

        return result;
    }

    public void AppendSnapshot(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Windows.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO snapshot_history (timestamp_ticks, window_key, percent, resets_at_ticks)
            VALUES ($ticks, $key, $percent, $resets)
            """;
        var ticks = command.Parameters.Add("$ticks", SqliteType.Integer);
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var percent = command.Parameters.Add("$percent", SqliteType.Real);
        var resets = command.Parameters.Add("$resets", SqliteType.Integer);
        foreach (var window in snapshot.Windows)
        {
            ticks.Value = snapshot.LastUpdated.UtcTicks;
            key.Value = window.Key;
            percent.Value = window.UtilizationPercent;
            resets.Value = window.ResetsAt is { } at ? at.UtcTicks : DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<HistoryPoint> GetSeries(string windowKey, DateTimeOffset since, DateTimeOffset until)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp_ticks, percent, resets_at_ticks FROM snapshot_history
            WHERE window_key = $key AND timestamp_ticks >= $from AND timestamp_ticks < $to ORDER BY timestamp_ticks
            """;
        command.Parameters.AddWithValue("$key", windowKey);
        command.Parameters.AddWithValue("$from", since.UtcTicks);
        command.Parameters.AddWithValue("$to", until.UtcTicks);
        using var reader = command.ExecuteReader();
        var result = new List<HistoryPoint>();
        while (reader.Read())
        {
            result.Add(new HistoryPoint(
                new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                reader.GetDouble(1),
                reader.IsDBNull(2) ? null : new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero)));
        }

        return result;
    }

    public IReadOnlyList<string> GetWindowKeys()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT window_key FROM snapshot_history ORDER BY window_key";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public int Prune(DateTimeOffset before)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshot_history WHERE timestamp_ticks < $before; DELETE FROM usage_events WHERE timestamp_ticks < $before;";
        command.Parameters.AddWithValue("$before", before.UtcTicks);
        return command.ExecuteNonQuery();
    }

    public void Clear()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshot_history; DELETE FROM usage_events; DELETE FROM scan_state;";
        command.ExecuteNonQuery();
    }

    private static TokenTotals ReadTotals(SqliteDataReader reader, int start) => new(
        reader.GetInt64(start),
        reader.GetInt64(start + 1),
        reader.GetInt64(start + 2),
        reader.GetInt64(start + 3),
        reader.GetInt64(start + 4),
        reader.GetInt32(start + 5));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        EnsureSchema(connection);
        return connection;
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (_sync)
        {
            if (_initialised)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS scan_state (
                    path TEXT PRIMARY KEY, offset INTEGER NOT NULL, length INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS usage_events (
                    message_id TEXT NOT NULL, request_id TEXT NOT NULL, timestamp_ticks INTEGER NOT NULL, model TEXT NOT NULL,
                    project TEXT, session_id TEXT, input INTEGER NOT NULL, output INTEGER NOT NULL,
                    cache_write_5m INTEGER NOT NULL, cache_write_1h INTEGER NOT NULL, cache_read INTEGER NOT NULL,
                    PRIMARY KEY (message_id, request_id));
                CREATE INDEX IF NOT EXISTS ix_usage_events_time ON usage_events (timestamp_ticks);
                CREATE TABLE IF NOT EXISTS snapshot_history (
                    timestamp_ticks INTEGER NOT NULL, window_key TEXT NOT NULL, percent REAL NOT NULL, resets_at_ticks INTEGER,
                    PRIMARY KEY (timestamp_ticks, window_key));
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
            _initialised = true;
        }
    }
}
