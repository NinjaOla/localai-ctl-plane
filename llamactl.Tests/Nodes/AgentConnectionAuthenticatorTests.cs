using llamactl.Contracts;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Tests.Nodes;

public sealed class AgentConnectionAuthenticatorTests
{
    [Theory]
    [InlineData("bootstrap-secret", true)]
    [InlineData("wrong-secret", false)]
    public async Task Registered_node_requires_matching_bootstrap_token(
        string suppliedToken,
        bool expected)
    {
        var nodeId = Guid.NewGuid();
        await using var fixture = await DatabaseFixture.CreateAsync(new NodeRecord
        {
            Id = nodeId,
            Name = "node-01",
            BootstrapTokenHash = BootstrapToken.Hash("bootstrap-secret"),
            Health = NodeHealth.PendingConfiguration,
        });
        var authenticator = new AgentConnectionAuthenticator(fixture);

        var authenticated = await authenticator.AuthenticateAsync(
            nodeId,
            suppliedToken,
            CancellationToken.None);

        Assert.Equal(expected, authenticated);
    }

    [Fact]
    public async Task Unknown_node_is_rejected()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var authenticator = new AgentConnectionAuthenticator(fixture);

        var authenticated = await authenticator.AuthenticateAsync(
            Guid.NewGuid(),
            "bootstrap-secret",
            CancellationToken.None);

        Assert.False(authenticated);
    }

    private sealed class DatabaseFixture(
        SqliteConnection connection,
        DbContextOptions<LlamactlDb> options)
        : IDbContextFactory<LlamactlDb>, IAsyncDisposable
    {
        public static async Task<DatabaseFixture> CreateAsync(NodeRecord? node = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LlamactlDb>()
                .UseSqlite(connection)
                .Options;

            await using var db = new LlamactlDb(options);
            await db.Database.EnsureCreatedAsync();
            if (node is not null)
            {
                db.Nodes.Add(node);
                await db.SaveChangesAsync();
            }

            return new DatabaseFixture(connection, options);
        }

        public LlamactlDb CreateDbContext() => new(options);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}