using llamactl.Web.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Web.Platform.NodeGateway;

internal sealed class AgentConnectionAuthenticator(IDbContextFactory<LlamactlDb> dbFactory)
{
    public async Task<bool> AuthenticateAsync(
        Guid nodeId,
        string bootstrapToken,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tokenHash = await db.Nodes
            .Where(node => node.Id == nodeId)
            .Select(node => node.BootstrapTokenHash)
            .SingleOrDefaultAsync(cancellationToken);

        return tokenHash is not null && BootstrapToken.Matches(bootstrapToken, tokenHash);
    }
}