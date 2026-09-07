namespace ClaudeTrayApp.Startup;

/// <summary>
/// One instance per session: a named mutex says whether another instance runs, and a named event lets a second
/// launch ask the first one to show its flyout before exiting.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\ClaudeUsageTray.Instance";
    private const string ActivationEventName = @"Local\ClaudeUsageTray.ShowFlyout";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    /// <summary>
    /// Claims the instance. Returns false when another instance already holds it; that instance has then been asked
    /// to show its flyout and the caller should exit.
    /// </summary>
    public static bool TryAcquire(out SingleInstance? instance) => TryAcquire(MutexName, ActivationEventName, out instance);

    /// <summary>
    /// Same as <see cref="TryAcquire(out SingleInstance?)"/> with the mutex and activation event names injected, so
    /// tests can exercise the real named-synchronization-object behaviour under unique per-run names instead of the
    /// production ones (which a real running instance on the same machine may already hold).
    /// </summary>
    internal static bool TryAcquire(string mutexName, string activationEventName, out SingleInstance? instance)
    {
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalExisting(activationEventName);
            instance = null;
            return false;
        }

        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, activationEventName);
        instance = new SingleInstance(mutex, activation);
        return true;
    }

    /// <summary>Runs <paramref name="onActivate"/> (on a thread-pool thread) every time a later launch signals.</summary>
    public void ListenForActivation(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        _registration ??= ThreadPool.RegisterWaitForSingleObject(
            _activation,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    onActivate();
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activation.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released by another thread than the one that took it: nothing to do at exit.
        }

        _mutex.Dispose();
    }

    private static void SignalExisting(string activationEventName)
    {
        try
        {
            using var activation = EventWaitHandle.OpenExisting(activationEventName);
            activation.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting up; it will show its icon shortly anyway.
        }
    }
}
