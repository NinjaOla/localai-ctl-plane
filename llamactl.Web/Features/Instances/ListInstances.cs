using System.Text.Json;
using Immediate.Handlers.Shared;
using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Features.Instances;

[Handler]
public sealed partial class ListInstances(IDbContextFactory<LlamactlDb> dbFactory)
{
    public sealed record Query(Guid? NodeId = null);
    public sealed record Response(
        Guid Id,
        Guid NodeId,
        string NodeName,
        InstanceSpec Spec,
        DesiredInstanceState DesiredState,
        ObservedInstanceState ObservedState,
        int? ProcessId,
        int? AdoptProcessId,
        string? Error,
        long Revision);

    private async ValueTask<IReadOnlyList<Response>> HandleAsync(
        Query query,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.Instances.AsNoTracking()
            .Where(instance => query.NodeId == null || instance.NodeId == query.NodeId)
            .Join(db.Nodes.AsNoTracking(), instance => instance.NodeId, node => node.Id,
                (instance, node) => new { Instance = instance, NodeName = node.Name })
            .OrderBy(item => item.NodeName).ThenBy(item => item.Instance.Name)
            .ToListAsync(cancellationToken);
        return records.Select(item => new Response(
            item.Instance.Id,
            item.Instance.NodeId,
            item.NodeName,
            JsonSerializer.Deserialize(item.Instance.SpecJson, LlamactlJsonContext.Default.InstanceSpec)!,
            item.Instance.DesiredState,
            item.Instance.ObservedState,
            item.Instance.ProcessId,
            item.Instance.AdoptProcessId,
            item.Instance.Error,
            item.Instance.Revision)).ToList();
    }
}