using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClaudeTrayApp.Core.ClaudeCode;

public interface IClaudeCodeVersionDetector
{
    ValueTask<string> GetVersionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>claude --version</c> once and caches the result for the User-Agent header.
/// Falls back to a known-good constant when Claude Code is not installed or does not answer in time.
/// </summary>
public sealed partial class ClaudeCodeVersionDetector : IClaudeCodeVersionDetector, IDisposable
{
    /// <summary>Version of the Claude Code CLI this code was probed against; used when detection fails.</summary>
    public const string FallbackVersion = "2.1.224";

    private static readonly TimeSpan DetectionTimeout = TimeSpan.FromSeconds(5);
    private readonly ILogger<ClaudeCodeVersionDetector> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;

    public ClaudeCodeVersionDetector(ILogger<ClaudeCodeVersionDetector> logger)
    {
        _logger = logger;
    }

    public async ValueTask<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cached ??= await DetectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Extracts "2.1.224" from output such as "2.1.224 (Claude Code)". Returns null when nothing version-like is present.</summary>
    public static string? ParseVersion(string? output) =>
        output is not null && VersionPattern().Match(output) is { Success: true } match ? match.Groups[1].Value : null;

    public void Dispose() => _gate.Dispose();

    private async Task<string> DetectAsync(CancellationToken cancellationToken)
    {
        try
        {
            // On Windows "claude" is an npm shim (.cmd), which needs the shell to resolve.
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/d /c claude --version")
                : new ProcessStartInfo("claude", "--version");
            startInfo.RedirectStandardOutput = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Fallback("process did not start");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DetectionTimeout);
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var version = ParseVersion(output);
                if (version is null)
                {
                    return Fallback("output not recognised");
                }

                _logger.LogDebug("Detected Claude Code {Version}", version);
                return version;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return Fallback("timed out");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return Fallback(ex.GetType().Name);
        }
    }

    private string Fallback(string reason)
    {
        _logger.LogDebug("Claude Code version not detected ({Reason}); using fallback {Version}", reason, FallbackVersion);
        return FallbackVersion;
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

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex VersionPattern();
}
