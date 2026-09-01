using System.Text.Json;
using Immediate.Handlers.Shared;
using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Features.Nodes.Configure;

[Handler]
public sealed partial class GetNodeConfiguration(IDbContextFactory<LlamactlDb> dbFactory)
{
    public sealed record Query(Guid NodeId);
    public sealed record Response(
        Guid NodeId,
        string Name,
        NodeConfiguration? Configuration,
        AgentAnnouncement? Announcement,
        IReadOnlyList<ValidationIssue> ValidationIssues);

    private async ValueTask<Response?> HandleAsync(Query query, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.Id == query.NodeId,
            cancellationToken);
        if (node is null)
            return null;

        return new(
            node.Id,
            node.Name,
            DeserializeConfiguration(node.ConfigurationJson),
            DeserializeAnnouncement(node.AnnouncementJson),
            DeserializeValidationIssues(node.ValidationIssuesJson));
    }

    private static NodeConfiguration? DeserializeConfiguration(string? json) => json is null
        ? null
        : JsonSerializer.Deserialize(json, LlamactlJsonContext.Default.NodeConfiguration);

    private static AgentAnnouncement? DeserializeAnnouncement(string? json) => json is null
        ? null
        : JsonSerializer.Deserialize(json, LlamactlJsonContext.Default.AgentAnnouncement);

    private static IReadOnlyList<ValidationIssue> DeserializeValidationIssues(string? json) => json is null
        ? []
        : JsonSerializer.Deserialize(json, LlamactlJsonContext.Default.IReadOnlyListValidationIssue) ?? [];
}