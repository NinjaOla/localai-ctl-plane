using System.Text.Json;
using Immediate.Handlers.Shared;
using llamactl.Contracts;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using llamactl.Web.Platform.Results;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Features.Presets;

[Handler]
public sealed partial class ManagePreset(IDbContextFactory<LlamactlDb> dbFactory, IAgentPresetGateway gateway)
{
    public sealed record Query(Guid NodeId);
    public sealed record Response(string Content, IReadOnlyList<string> Sections);

    private async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Nodes.AnyAsync(node => node.Id == query.NodeId, cancellationToken))
            return Result<Response>.NotFound("Node was not found.");
        var content = await gateway.ReadAsync(query.NodeId, cancellationToken);
        if (content is null)
            return Result<Response>.NodeUnreachable("Node is not connected.");
        try
        {
            var document = PresetDocument.Parse(content);
            return Result<Response>.Success(new(content, document.Sections.Select(section => section.Name).ToList()));
        }
        catch (FormatException exception)
        {
            return Result<Response>.Validation(exception.Message);
        }
    }
}

[Handler]
public sealed partial class PreviewPreset(IDbContextFactory<LlamactlDb> dbFactory)
{
    public sealed record Command(Guid NodeId, string Original, string Updated);
    public sealed record Response(string Diff, IReadOnlyList<string> Errors, bool RouterRestartRequired);

    private async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var announcementJson = await db.Nodes.Where(node => node.Id == command.NodeId)
            .Select(node => node.AnnouncementJson).SingleOrDefaultAsync(cancellationToken);
        if (announcementJson is null)
            return Result<Response>.Validation("The node has not announced its llama.cpp flag schema.");
        try
        {
            var announcement = JsonSerializer.Deserialize(announcementJson, LlamactlJsonContext.Default.AgentAnnouncement)!;
            var flags = announcement.Runtimes.SingleOrDefault(runtime => runtime.Id == RuntimeId.LlamaCpp)?.FlagSchema.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
            var document = PresetDocument.Parse(command.Updated);
            return Result<Response>.Success(new(PresetDiff.Create(command.Original, command.Updated), document.Validate(flags),
                !string.Equals(command.Original, command.Updated, StringComparison.Ordinal)));
        }
        catch (FormatException exception)
        {
            return Result<Response>.Validation(exception.Message);
        }
    }
}

[Handler]
public sealed partial class SavePreset(IDbContextFactory<LlamactlDb> dbFactory, IAgentPresetGateway gateway)
{
    public sealed record Command(Guid NodeId, string Content);
    public sealed record Response(bool Saved);

    private async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken cancellationToken)
    {
        PresetDocument document;
        try { document = PresetDocument.Parse(command.Content); }
        catch (FormatException exception) { return Result<Response>.Validation(exception.Message); }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var announcementJson = await db.Nodes.Where(node => node.Id == command.NodeId)
            .Select(node => node.AnnouncementJson).SingleOrDefaultAsync(cancellationToken);
        if (announcementJson is null)
            return Result<Response>.Validation("The node has not announced its llama.cpp flag schema.");
        var announcement = JsonSerializer.Deserialize(announcementJson, LlamactlJsonContext.Default.AgentAnnouncement)!;
        var flags = announcement.Runtimes.SingleOrDefault(runtime => runtime.Id == RuntimeId.LlamaCpp)?.FlagSchema.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
        var errors = document.Validate(flags);
        if (errors.Count > 0)
            return Result<Response>.Validation(string.Join(" ", errors));
        return await gateway.WriteAsync(command.NodeId, command.Content, cancellationToken)
            ? Result<Response>.Success(new(true))
            : Result<Response>.NodeUnreachable("Node is not connected.");
    }
}