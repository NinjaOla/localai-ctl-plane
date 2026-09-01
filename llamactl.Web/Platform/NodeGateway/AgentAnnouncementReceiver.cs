using System.Text.Json;
using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Platform.NodeGateway;

internal sealed class AgentAnnouncementReceiver(IDbContextFactory<LlamactlDb> dbFactory)
{
    public async Task ReceiveAsync(
        Guid authenticatedNodeId,
        Envelope<AgentAnnouncement> envelope,
        CancellationToken cancellationToken)
    {
        if (envelope.NodeId != authenticatedNodeId)
            throw new InvalidOperationException("Announcement node ID does not match the authenticated connection.");

        if (envelope.SchemaVersion != Protocol.SchemaVersion)
            throw new InvalidOperationException($"Unsupported protocol schema version {envelope.SchemaVersion}.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.SingleAsync(
            candidate => candidate.Id == authenticatedNodeId,
            cancellationToken);
        var llamaCpp = envelope.Payload.Runtimes.SingleOrDefault(
            runtime => runtime.Id == RuntimeId.LlamaCpp);

        node.LastSeen = DateTimeOffset.UtcNow;
        node.GpuName = envelope.Payload.Description.GpuName;
        node.VramTotalMiB = envelope.Payload.Description.VramTotalMiB;
        node.LlamaCppVersion = llamaCpp?.Version ?? envelope.Payload.Description.LlamaCppVersion;
        node.RocmVersion = envelope.Payload.Description.RocmVersion;
        node.AnnouncementJson = JsonSerializer.Serialize(
            envelope.Payload,
            LlamactlJsonContext.Default.AgentAnnouncement);
        node.Version++;

        await db.SaveChangesAsync(cancellationToken);
    }
}