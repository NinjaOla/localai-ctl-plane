using System.Text.Json;
using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Platform.NodeGateway;

internal sealed class DesiredStateStore(IDbContextFactory<LlamactlDb> dbFactory)
{
    public async Task<AgentDesiredState> GetAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.AsNoTracking().SingleAsync(
            candidate => candidate.Id == nodeId,
            cancellationToken);
        var records = await db.Instances.AsNoTracking()
            .Where(instance => instance.NodeId == nodeId)
            .OrderBy(instance => instance.Name)
            .ToListAsync(cancellationToken);
        var instances = records.Select(record => new DesiredInstance(
            record.Id,
            record.Revision,
            JsonSerializer.Deserialize(record.SpecJson, LlamactlJsonContext.Default.InstanceSpec)
                ?? throw new InvalidOperationException($"Instance {record.Id} has no specification."),
            record.DesiredState,
            record.AdoptProcessId)).ToList();
        var configuration = node.ConfigurationJson is null
            ? null
            : JsonSerializer.Deserialize(node.ConfigurationJson, LlamactlJsonContext.Default.NodeConfiguration);
        return new(node.DesiredStateVersion, configuration, instances);
    }

    public async Task ReportAsync(
        Guid nodeId,
        ReconciliationReport report,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.SingleAsync(candidate => candidate.Id == nodeId, cancellationToken);
        if (report.DesiredStateVersion != node.DesiredStateVersion)
            return;

        node.ValidationIssuesJson = JsonSerializer.Serialize(
            report.ValidationIssues,
            LlamactlJsonContext.Default.IReadOnlyListValidationIssue);
        node.LastSeen = DateTimeOffset.UtcNow;
        node.Health = report.ValidationIssues.Any(issue => issue.Severity == ValidationSeverity.Error)
            ? NodeHealth.Degraded
            : NodeHealth.Healthy;
        node.Version++;

        var observedById = report.Instances.ToDictionary(instance => instance.Id);
        var records = await db.Instances
            .Where(instance => instance.NodeId == nodeId)
            .ToListAsync(cancellationToken);
        foreach (var record in records)
        {
            if (!observedById.TryGetValue(record.Id, out var observed))
                continue;
            record.ObservedState = observed.State;
            record.ProcessId = observed.ProcessId;
            record.Error = observed.Error;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}