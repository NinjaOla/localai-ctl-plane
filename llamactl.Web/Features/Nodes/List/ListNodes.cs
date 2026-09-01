using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Features.Nodes.List;

[Handler]
[MapGet("/api/v1/nodes")]
public sealed partial class ListNodes(IDbContextFactory<LlamactlDb> dbFactory)
{
    public sealed record Query;

    public sealed record Response(
        Guid Id,
        string Name,
        NodeHealth Health,
        DateTimeOffset? LastSeen,
        string? GpuName,
        long? VramTotalMiB,
        string? LlamaCppVersion,
        string? RocmVersion);

    private async ValueTask<IReadOnlyList<Response>> HandleAsync(
        Query _,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.Nodes
            .AsNoTracking()
            .OrderBy(node => node.Name)
            .Select(node => new Response(
                node.Id,
                node.Name,
                node.Health,
                node.LastSeen,
                node.GpuName,
                node.VramTotalMiB,
                node.LlamaCppVersion,
                node.RocmVersion))
            .ToListAsync(cancellationToken);
    }
}