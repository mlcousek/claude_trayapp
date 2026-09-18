using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeTrayApp.Core.ClaudeCode;

/// <summary>A process to run headless. A null value in <see cref="Environment"/> removes that variable from the child.</summary>
public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

/// <summary>How a run ended. <see cref="ExitCode"/> is null when the process never started or was killed on timeout.</summary>
public sealed record ProcessRunResult(int? ExitCode, bool TimedOut, TimeSpan Elapsed, string? FailureReason)
{
    public bool Started => ExitCode is not null || TimedOut;
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs a process to completion with its input closed at once and its output read and discarded. A process that
    /// cannot start or does not exit within <paramref name="timeout"/> is a result, not an exception; on timeout the
    /// whole process tree is killed.
    /// </summary>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IProcessRunner"/>. No window, no inherited console, and nothing the child prints is kept:
/// callers that need a child's output do not use this runner.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly TimeProvider _clock;

    public ProcessRunner(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _clock.GetTimestamp();

        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new ProcessRunResult(null, false, _clock.GetElapsedTime(started), ex.GetType().Name);
        }

        if (process is null)
        {
            return new ProcessRunResult(null, false, _clock.GetElapsedTime(started), "process did not start");
        }

        using (process)
        {
            // End of input straight away: the child must never wait for a prompt.
            process.StandardInput.Close();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var drainOutput = DrainAsync(process.StandardOutput, timeoutSource.Token);
            var drainError = DrainAsync(process.StandardError, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                await Task.WhenAll(drainOutput, drainError).ConfigureAwait(false);
                return new ProcessRunResult(process.ExitCode, false, _clock.GetElapsedTime(started), null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new ProcessRunResult(null, true, _clock.GetElapsedTime(started), "timed out");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
                // Discarded on purpose: the child's output may carry anything, and nothing here keeps or logs it.
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The pipe closed or the wait timed out; either way there is nothing left to read.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already exited; nothing to do.
        }
    }
}
