using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Platform.NodeGateway;

internal sealed class AgentLogReceiver(
    IDbContextFactory<LlamactlDb> dbFactory,
    InstanceLogStore store)
{
    public async Task ReceiveAsync(
        Guid authenticatedNodeId,
        IReadOnlyList<ProcessLogLine> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
            return;
        var instanceIds = lines.Select(line => line.InstanceId).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ownedCount = await db.Instances.CountAsync(instance =>
            instance.NodeId == authenticatedNodeId && instanceIds.Contains(instance.Id),
            cancellationToken);
        if (ownedCount != instanceIds.Count)
            throw new InvalidOperationException("Log batch contains an instance not owned by this node.");
        store.Append(lines);
    }
}