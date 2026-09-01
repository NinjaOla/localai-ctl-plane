using llamactl.Web.Features.Nodes.Onboard;
using llamactl.Web.Platform.Persistence;
using llamactl.Web.Platform.Results;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Tests.Nodes;

public sealed class OnboardNodeTests
{
    [Fact]
    public async Task Onboarding_hashes_token_and_rejects_duplicate_name()
    {
        var cancellationToken = CancellationToken.None;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<LlamactlDb>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);

        await using (var db = new LlamactlDb(options))
            await db.Database.EnsureCreatedAsync(cancellationToken);

        var handler = CreateHandler(factory);
        var command = new OnboardNode.Command
        {
            Name = "node-01",
            BootstrapToken = "bootstrap-secret",
        };

        var created = await handler.HandleAsync(command, cancellationToken);
        var duplicate = await handler.HandleAsync(command, cancellationToken);

        Assert.True(created.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, duplicate.Error?.Kind);

        await using var verificationDb = new LlamactlDb(options);
        var stored = await verificationDb.Nodes.SingleAsync(cancellationToken);
        Assert.NotEqual(command.BootstrapToken, stored.BootstrapTokenHash);
        Assert.Equal(64, stored.BootstrapTokenHash.Length);
    }

    private static OnboardNode.Handler CreateHandler(IDbContextFactory<LlamactlDb> factory)
    {
        var container = new OnboardNode(factory);
        return new OnboardNode.Handler(new OnboardNode.HandleBehavior(container));
    }

    private sealed class TestDbContextFactory(DbContextOptions<LlamactlDb> options)
        : IDbContextFactory<LlamactlDb>
    {
        public LlamactlDb CreateDbContext() => new(options);
    }
}