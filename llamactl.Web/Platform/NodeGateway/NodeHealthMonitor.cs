using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Platform.NodeGateway;

internal sealed class NodeHealthMonitor(
    IDbContextFactory<LlamactlDb> dbFactory,
    TimeProvider timeProvider,
    ILogger<NodeHealthMonitor> logger) : BackgroundService
{
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await MarkUnreachableNodesAsync(stoppingToken);
    }

    internal async Task<int> MarkUnreachableNodesAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var cutoff = timeProvider.GetUtcNow() - HeartbeatTimeout;
        var candidates = await db.Nodes
            .Where(node => node.LastSeen != null
                && node.Health != NodeHealth.Unreachable)
            .ToListAsync(cancellationToken);
        var staleNodes = candidates.Where(node => node.LastSeen < cutoff).ToList();
        foreach (var node in staleNodes)
        {
            node.Health = NodeHealth.Unreachable;
            node.Version++;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (staleNodes.Count > 0)
            logger.LogInformation("Marked {NodeCount} stale nodes unreachable", staleNodes.Count);
        return staleNodes.Count;
    }
}