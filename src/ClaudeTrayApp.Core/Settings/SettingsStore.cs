using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Core.Settings;

/// <summary>
/// Owns settings.json: loads it (writing the defaults on first run so the file is there to edit), saves changes
/// atomically, and reloads when something else edits the file. A file that cannot be parsed is left untouched and
/// reported through <see cref="LastError"/>; the previous settings stay in force. An account's own file is opened with
/// <c>excludeGlobals</c>: the shared settings (<see cref="AppSettings.GlobalPropertyNames"/>) belong to the root file
/// and are neither written to it nor read from it. The root file is shared by every account's process, so every write
/// (first-run defaults, saves, and an update's read-change-write) holds a named mutex for that path.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    /// <summary>How long a write waits for another process's write to the same file before going ahead anyway.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ILogger<SettingsStore> _logger;
    private readonly TimeSpan _debounce;
    private readonly bool _excludeGlobals;
    private readonly string _mutexName;
    private readonly object _sync = new();
    private AppSettings _current = AppSettings.Default;
    private string? _lastWritten;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _pendingReload;

    public SettingsStore(string path, ILogger<SettingsStore> logger, TimeSpan? debounce = null, bool excludeGlobals = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(400);
        _excludeGlobals = excludeGlobals;
        _mutexName = MutexNameFor(Path);
    }

    /// <summary>Raised after the settings changed, from a save or an external edit. May fire on any thread.</summary>
    public event EventHandler<AppSettings>? Changed;

    public string Path { get; }

    public AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>Why the last load or save failed, in user-facing words; null when the last operation succeeded.</summary>
    public string? LastError { get; private set; }

    /// <summary>Reads the file, or writes the defaults when there is none. Returns the settings in force afterwards.</summary>
    public AppSettings Load()
    {
        using (AcquireFileLock())
        {
            if (!File.Exists(Path))
            {
                _logger.LogInformation("No settings file at {Path}; writing the defaults", Path);
                SaveUnlocked(AppSettings.Default);
                return Current;
            }
        }

        Reload();
        return Current;
    }

    /// <summary>Re-reads the file. Returns true when the settings in force changed.</summary>
    public bool Reload()
    {
        string text;
        try
        {
            text = ReadWithRetry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "settings.json could not be read: " + ex.Message;
            _logger.LogWarning(ex, "Settings file {Path} could not be read; keeping the current settings", Path);
            return false;
        }

        if (text == _lastWritten)
        {
            return false;
        }

        // Someone else wrote the file since. Should they later restore exactly what this store last wrote (another
        // account's instance switching a shared setting back), that text is theirs and must be applied, not skipped.
        _lastWritten = null;

        AppSettings parsed;
        try
        {
            parsed = (JsonSerializer.Deserialize<AppSettings>(text, JsonOptions) ?? AppSettings.Default).Normalized();
            if (_excludeGlobals)
            {
                parsed = parsed.WithoutGlobals();
            }
        }
        catch (JsonException ex)
        {
            LastError = "settings.json is not valid JSON; the file was left as it is and the previous settings stay in force. " + ex.Message;
            _logger.LogWarning(ex, "Settings file {Path} is not valid JSON; keeping the current settings", Path);
            return false;
        }

        LastError = null;
        return Publish(parsed);
    }

    /// <summary>Normalizes and writes the settings, then raises <see cref="Changed"/> if anything differs.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using (AcquireFileLock())
        {
            SaveUnlocked(settings);
        }
    }

    private void SaveUnlocked(AppSettings settings)
    {
        var normalized = settings.Normalized();
        var text = Serialize(normalized);
        if (_excludeGlobals)
        {
            normalized = normalized.WithoutGlobals();
        }

        try
        {
            WriteAtomically(text);
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "settings.json could not be written: " + ex.Message;
            _logger.LogWarning(ex, "Settings file {Path} could not be written", Path);
        }

        Publish(normalized);
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the file as it is now. Another process may share the file (the root
    /// settings.json is written by every account), so it is read again first: an edit made there moments ago, which
    /// the debounced watcher has not picked up yet, is kept rather than overwritten. The read, the change and the write
    /// happen under the file's lock, so two processes updating at once each keep the other's edit.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using (AcquireFileLock())
        {
            if (File.Exists(Path))
            {
                Reload();
            }

            SaveUnlocked(change(Current));
        }
    }

    /// <summary>Starts reloading on external edits, debounced so an editor's several writes count once.</summary>
    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, System.IO.Path.GetFileName(Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Settings file watcher could not be started; edits to {Path} apply after a restart", Path);
        }
    }

    /// <summary>Safe to call twice: the default account registers one store under two names.</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_sync)
        {
            _pendingReload?.Cancel();
            _pendingReload?.Dispose();
            _pendingReload = null;
        }
    }

    private string Serialize(AppSettings settings)
    {
        if (!_excludeGlobals)
        {
            return JsonSerializer.Serialize(settings, JsonOptions) + Environment.NewLine;
        }

        var node = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        foreach (var name in AppSettings.GlobalPropertyNames)
        {
            node.Remove(name);
        }

        return node.ToJsonString(JsonOptions) + Environment.NewLine;
    }

    private bool Publish(AppSettings settings)
    {
        lock (_sync)
        {
            if (_current == settings)
            {
                return false;
            }

            _current = settings;
        }

        Changed?.Invoke(this, settings);
        return true;
    }

    private string ReadWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(Path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private void WriteAtomically(string text)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A temp name of its own, so a writer that went ahead after a lock timeout cannot move another's half-written file.
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, Path, overwrite: true);
            _lastWritten = text;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>"Local\ClaudeUsageTray.Settings.&lt;16 hex&gt;": one name per settings file, whatever its spelling.</summary>
    internal static string MutexNameFor(string fullPath) =>
        @"Local\ClaudeUsageTray.Settings." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())).AsSpan(0, 8));

    private FileLock AcquireFileLock() => new(_mutexName, LockTimeout, _logger);

    /// <summary>
    /// Holds the named mutex of one settings file. Taken and released on the same thread around synchronous work only,
    /// as a mutex requires. A wait that times out or finds the mutex abandoned by a crashed process goes ahead: a write
    /// that might race is better than a setting that silently does not save.
    /// </summary>
    private sealed class FileLock : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly bool _owned;

        public FileLock(string name, TimeSpan timeout, ILogger logger)
        {
            _mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                _owned = _mutex.WaitOne(timeout);
                if (!_owned)
                {
                    logger.LogWarning("Another process held the settings file lock for {Timeout}; writing anyway", timeout);
                }
            }
            catch (AbandonedMutexException)
            {
                _owned = true;
            }
        }

        public void Dispose()
        {
            if (_owned)
            {
                _mutex.ReleaseMutex();
            }

            _mutex.Dispose();
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        CancellationTokenSource source;
        lock (_sync)
        {
            _pendingReload?.Cancel();
            _pendingReload?.Dispose();
            _pendingReload = source = new CancellationTokenSource();
        }

        var token = source.Token;
        _ = Task.Delay(_debounce, token).ContinueWith(
            _ =>
            {
                if (!token.IsCancellationRequested && Reload())
                {
                    _logger.LogInformation("Settings reloaded from {Path}", Path);
                }
            },
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }
}
