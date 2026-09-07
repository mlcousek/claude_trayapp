using ClaudeTrayApp.Core.Polling;
using Microsoft.Extensions.Hosting;

namespace ClaudeTrayApp.Hosting;

/// <summary>Runs the polling loop for the lifetime of the host.</summary>
internal sealed class UsagePollerService : BackgroundService
{
    private readonly UsagePoller _poller;

    public UsagePollerService(UsagePoller poller)
    {
        _poller = poller;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _poller.RunAsync(stoppingToken);
}
