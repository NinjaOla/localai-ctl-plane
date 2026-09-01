using System.Text.Json;
using Immediate.Handlers.Shared;
using llamactl.Contracts;
using llamactl.Web.Features.Nodes.Configure;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using llamactl.Web.Platform.Results;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Features.Instances;

public enum InstanceAction
{
    Upsert,
    Start,
    Stop,
    Restart,
    Delete
}

[Handler]
public sealed partial class ManageInstance(
    IDbContextFactory<LlamactlDb> dbFactory,
    IDesiredStateNotifier notifier)
{
    public sealed record Command(
        InstanceAction Action,
        Guid NodeId,
        Guid? InstanceId = null,
        InstanceSpec? Spec = null,
        int? AdoptProcessId = null);

    public sealed record Response(Guid? InstanceId, long DesiredStateVersion);

    private async ValueTask<Result<Response>> HandleAsync(
        Command command,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.SingleOrDefaultAsync(candidate => candidate.Id == command.NodeId, cancellationToken);
        if (node is null)
            return Result<Response>.NotFound("Node was not found.");
        if (node.ConfigurationJson is null)
            return Result<Response>.Validation("Configure the node before managing instances.");

        var instance = command.InstanceId is null
            ? null
            : await db.Instances.SingleOrDefaultAsync(
                candidate => candidate.Id == command.InstanceId && candidate.NodeId == command.NodeId,
                cancellationToken);
        if (command.Action != InstanceAction.Upsert && instance is null)
            return Result<Response>.NotFound("Instance was not found.");

        Guid? instanceId;
        if (command.Action == InstanceAction.Upsert)
        {
            if (command.Spec is null)
                return Result<Response>.Validation("An instance specification is required.");
            var configuration = JsonSerializer.Deserialize(
                node.ConfigurationJson,
                LlamactlJsonContext.Default.NodeConfiguration)!;
            var usedPorts = await db.Instances
                .Where(candidate => candidate.NodeId == command.NodeId && candidate.Id != command.InstanceId)
                .Select(candidate => candidate.SpecJson)
                .ToListAsync(cancellationToken);
            var occupied = usedPorts
                .Select(json => JsonSerializer.Deserialize(json, LlamactlJsonContext.Default.InstanceSpec)?.Port)
                .OfType<int>()
                .ToHashSet();
            var assignedPort = command.Spec.Port ?? Enumerable
                .Range(configuration.PortRange.Start, configuration.PortRange.End - configuration.PortRange.Start + 1)
                .FirstOrDefault(port => !occupied.Contains(port));
            if (assignedPort == 0)
                return Result<Response>.Conflict("No ports are available in the node's configured range.");
            if (occupied.Contains(assignedPort))
                return Result<Response>.Conflict($"Port {assignedPort} is already assigned on this node.");
            var effectiveSpec = command.Spec with { Port = assignedPort };
            var validationError = ValidateSpec(effectiveSpec, configuration);
            if (validationError is not null)
                return Result<Response>.Validation(validationError);
            var duplicate = await db.Instances.AnyAsync(candidate =>
                candidate.NodeId == command.NodeId
                && candidate.Name == command.Spec.Name.Trim()
                && candidate.Id != command.InstanceId,
                cancellationToken);
            if (duplicate)
                return Result<Response>.Conflict($"An instance named '{command.Spec.Name.Trim()}' already exists on this node.");

            if (instance is null)
            {
                instance = new InstanceRecord
                {
                    Id = Guid.NewGuid(),
                    NodeId = command.NodeId,
                    Name = effectiveSpec.Name.Trim(),
                    SpecJson = Serialize(effectiveSpec),
                    DesiredState = DesiredInstanceState.Stopped,
                    AdoptProcessId = command.AdoptProcessId,
                    Revision = 1,
                };
                db.Instances.Add(instance);
            }
            else
            {
                instance.Name = effectiveSpec.Name.Trim();
                instance.SpecJson = Serialize(effectiveSpec);
                instance.AdoptProcessId = command.AdoptProcessId;
                instance.Revision++;
            }
            instanceId = instance.Id;
        }
        else if (command.Action == InstanceAction.Delete)
        {
            instanceId = instance!.Id;
            db.Instances.Remove(instance);
        }
        else
        {
            instanceId = instance!.Id;
            instance.DesiredState = command.Action == InstanceAction.Stop
                ? DesiredInstanceState.Stopped
                : DesiredInstanceState.Running;
            if (command.Action == InstanceAction.Restart)
                instance.Revision++;
            instance.Error = null;
        }

        node.DesiredStateVersion++;
        node.Version++;
        await db.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(node.Id, node.DesiredStateVersion, cancellationToken);
        return Result<Response>.Success(new(instanceId, node.DesiredStateVersion));
    }

    private static string? ValidateSpec(InstanceSpec spec, NodeConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(spec.Name))
            return "Instance name is required.";
        if (spec.Runtime != RuntimeId.LlamaCpp)
            return "Only the llama.cpp runtime is currently supported.";
        if (spec.Kind != InstanceKind.Managed)
            return "Daily-operation instances must be managed.";
        if (spec.Profile is not ("router" or "single"))
            return "Profile must be 'router' or 'single'.";
        if (spec.Profile == "single" && string.IsNullOrWhiteSpace(spec.ModelRef))
            return "Single-model instances require a model path.";
        if (spec.Port is { } port && !configuration.PortRange.Contains(port))
            return $"Port must be within {configuration.PortRange.Start}-{configuration.PortRange.End}.";
        return null;
    }

    private static string Serialize(InstanceSpec spec) =>
        JsonSerializer.Serialize(spec, LlamactlJsonContext.Default.InstanceSpec);
}