namespace ClaudeTrayApp.Core.Updates;

/// <summary>
/// One update check at a time across every account's process: an exclusively opened lock file, held from reading the
/// check state to saving it. A file rather than a mutex because the check awaits a request and a mutex is tied to one
/// thread; Windows releases the file when a process dies, so a crash never leaves it stuck.
/// </summary>
public static class UpdateCheckLock
{
    /// <summary>The held lock, or null when another process holds it (or the file cannot be opened at all).</summary>
    public static IDisposable? TryAcquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
