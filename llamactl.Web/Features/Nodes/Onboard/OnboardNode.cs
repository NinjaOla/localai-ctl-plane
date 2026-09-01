using System.Security.Cryptography;
using System.Text;
using Immediate.Apis.Shared;
using Immediate.Handlers.Shared;
using Immediate.Validations.Shared;
using llamactl.Contracts;
using llamactl.Web.Platform.Persistence;
using llamactl.Web.Platform.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Features.Nodes.Onboard;

[Handler]
[MapPost("/api/v1/nodes")]
public sealed partial class OnboardNode(IDbContextFactory<LlamactlDb> dbFactory)
{
    [Validate]
    public sealed partial record Command : IValidationTarget<Command>
    {
        [NotEmpty]
        public required string Name { get; init; }

        [NotEmpty]
        public required string BootstrapToken { get; init; }
    }

    public sealed record Response(Guid Id, string Name, NodeHealth Health);

    private async ValueTask<Result<Response>> HandleAsync(
        Command command,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalizedName = command.Name.Trim();

        if (await db.Nodes.AnyAsync(node => node.Name == normalizedName, cancellationToken))
            return Result<Response>.Conflict($"A node named '{normalizedName}' already exists.");

        var node = new NodeRecord
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            BootstrapTokenHash = HashToken(command.BootstrapToken),
        };

        db.Nodes.Add(node);
        await db.SaveChangesAsync(cancellationToken);

        return Result<Response>.Success(new(node.Id, node.Name, node.Health));
    }

    internal static IResult TransformResult(Result<Response> result) => result switch
    {
        { IsSuccess: true, Value: not null } => TypedResults.Created($"/api/v1/nodes/{result.Value.Id}", result.Value),
        { Error.Kind: ErrorKind.Conflict } => TypedResults.Problem(
            result.Error.Message,
            statusCode: StatusCodes.Status409Conflict,
            title: "Node already exists"),
        _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}