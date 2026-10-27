using ClaudeTrayApp.Core.Account;

namespace ClaudeTrayApp.Startup;

/// <summary>
/// One instance per account per session: a named mutex says whether another instance runs, and a named event lets a
/// second launch ask the first one to show its flyout before exiting. The default account keeps the original names;
/// an account started with <c>--claude-dir</c> appends its key, so accounts run side by side.
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
    public static bool TryAcquire(AccountContext account, out SingleInstance? instance)
    {
        ArgumentNullException.ThrowIfNull(account);
        return TryAcquire(MutexNameFor(account.Key), ActivationEventNameFor(account.Key), out instance);
    }

    /// <summary>Whether the default account's instance is running in this session. Opens nothing it keeps.</summary>
    public static bool IsDefaultAccountRunning() => IsRunning(MutexName);

    internal static string MutexNameFor(string? accountKey) => accountKey is null ? MutexName : MutexName + "." + accountKey;

    internal static string ActivationEventNameFor(string? accountKey) => accountKey is null ? ActivationEventName : ActivationEventName + "." + accountKey;

    internal static bool IsRunning(string mutexName)
    {
        if (!Mutex.TryOpenExisting(mutexName, out var mutex))
        {
            return false;
        }

        mutex.Dispose();
        return true;
    }

    /// <summary>
    /// Same as <see cref="TryAcquire(AccountContext, out SingleInstance?)"/> with the mutex and activation event names injected, so
    /// tests can exercise the real named-synchronization-object behaviour under unique per-run names instead of the
    /// production ones (which a real running instance on the same machine may already hold).
    /// </summary>
    internal static bool TryAcquire(string mutexName, string activationEventName, out SingleInstance? instance)
    {
        // The event comes first, so it exists whenever the mutex does: a second launch arriving while the first is
        // still starting finds it and leaves it signalled, and the first picks the signal up once it listens.
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, activationEventName);
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            activation.Set();
            activation.Dispose();
            instance = null;
            return false;
        }

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
}
