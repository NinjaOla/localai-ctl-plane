using System.Text.Json;
using Immediate.Handlers.Shared;
using llamactl.Contracts;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using llamactl.Web.Platform.Results;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Features.Nodes.Configure;

[Handler]
public sealed partial class ConfigureNode(
    IDbContextFactory<LlamactlDb> dbFactory,
    IDesiredStateNotifier notifier)
{
    public sealed record Command(Guid NodeId, NodeConfiguration Configuration);
    public sealed record Response(Guid NodeId, long DesiredStateVersion);

    private async ValueTask<Result<Response>> HandleAsync(
        Command command,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(command.Configuration);
        if (validationError is not null)
            return Result<Response>.Validation(validationError);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.SingleOrDefaultAsync(
            candidate => candidate.Id == command.NodeId,
            cancellationToken);
        if (node is null)
            return Result<Response>.NotFound("Node was not found.");

        node.ConfigurationJson = JsonSerializer.Serialize(
            command.Configuration,
            LlamactlJsonContext.Default.NodeConfiguration);
        node.DesiredStateVersion++;
        node.Health = NodeHealth.PendingConfiguration;
        node.Version++;
        await db.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(node.Id, node.DesiredStateVersion, cancellationToken);

        return Result<Response>.Success(new(node.Id, node.DesiredStateVersion));
    }

    internal static string? Validate(NodeConfiguration configuration)
    {
        var paths = new[]
        {
            configuration.Paths.LlamaBin,
            configuration.Paths.LlamaSource,
            configuration.Paths.Rocm,
            configuration.Paths.ModelsRoot,
            configuration.Paths.HfHome,
            configuration.Paths.FlatDir,
            configuration.Paths.PresetFile,
            configuration.Paths.EmptyCache,
            configuration.Paths.SystemdDir,
            configuration.Paths.ConfigRepo,
        };
        if (paths.Any(string.IsNullOrWhiteSpace))
            return "Every path is required.";
        if (configuration.VramBudgetMiB <= 0)
            return "VRAM budget must be greater than zero.";
        if (configuration.PortRange.Start is < 1 or > 65_535
            || configuration.PortRange.End is < 1 or > 65_535
            || configuration.PortRange.Start > configuration.PortRange.End)
            return "Port range must be ordered and between 1 and 65535.";
        if (configuration.DefaultGpuLayers < 0)
            return "Default GPU layers cannot be negative.";
        return null;
    }
}