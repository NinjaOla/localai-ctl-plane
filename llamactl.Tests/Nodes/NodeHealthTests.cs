using llamactl.Contracts;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace llamactl.Tests.Nodes;

public sealed class NodeHealthTests
{
    [Theory]
    [InlineData(false, NodeHealth.Healthy)]
    [InlineData(true, NodeHealth.Degraded)]
    public async Task Heartbeat_updates_last_seen_and_reported_health(
        bool hasError,
        NodeHealth expectedHealth)
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        var nodeId = Guid.NewGuid();
        await using var fixture = await DatabaseFixture.CreateAsync([
            CreateNode(nodeId, announcementJson: "{}")
        ]);
        var receiver = new AgentHeartbeatReceiver(fixture, clock);
        var issues = hasError
            ? new[] { new ValidationIssue("paths.modelsRoot", "Not writable.", ValidationSeverity.Error) }
            : [];

        await receiver.ReceiveAsync(
            nodeId,
            new Envelope<AgentHeartbeat>(
                Guid.NewGuid(), null, nodeId, Protocol.SchemaVersion, now, new AgentHeartbeat(issues)),
            CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Nodes.SingleAsync();
        Assert.Equal(now, stored.LastSeen);
        Assert.Equal(expectedHealth, stored.Health);
    }

    [Fact]
    public async Task Monitor_marks_only_stale_connected_nodes_unreachable()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(now);
        var stale = CreateNode(Guid.NewGuid(), "{}", now - NodeHealthMonitor.HeartbeatTimeout - TimeSpan.FromSeconds(1));
        var current = CreateNode(Guid.NewGuid(), "{}", now - NodeHealthMonitor.HeartbeatTimeout);
        var pending = CreateNode(Guid.NewGuid());
        await using var fixture = await DatabaseFixture.CreateAsync([stale, current, pending]);
        var monitor = new NodeHealthMonitor(fixture, clock, NullLogger<NodeHealthMonitor>.Instance);

        var updated = await monitor.MarkUnreachableNodesAsync(CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var nodes = await db.Nodes.OrderBy(node => node.Name).ToListAsync();
        Assert.Equal(1, updated);
        Assert.Equal(NodeHealth.Unreachable, nodes.Single(node => node.Id == stale.Id).Health);
        Assert.Equal(NodeHealth.Healthy, nodes.Single(node => node.Id == current.Id).Health);
        Assert.Equal(NodeHealth.PendingConfiguration, nodes.Single(node => node.Id == pending.Id).Health);
    }

    private static NodeRecord CreateNode(
        Guid id,
        string? announcementJson = null,
        DateTimeOffset? lastSeen = null) => new()
        {
            Id = id,
            Name = id.ToString(),
            BootstrapTokenHash = BootstrapToken.Hash("secret"),
            AnnouncementJson = announcementJson,
            LastSeen = lastSeen,
            Health = announcementJson is null ? NodeHealth.PendingConfiguration : NodeHealth.Healthy,
        };

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class DatabaseFixture(
        SqliteConnection connection,
        DbContextOptions<LlamactlDb> options)
        : IDbContextFactory<LlamactlDb>, IAsyncDisposable
    {
        public static async Task<DatabaseFixture> CreateAsync(IReadOnlyList<NodeRecord> nodes)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LlamactlDb>().UseSqlite(connection).Options;
            await using var db = new LlamactlDb(options);
            await db.Database.EnsureCreatedAsync();
            db.Nodes.AddRange(nodes);
            await db.SaveChangesAsync();
            return new DatabaseFixture(connection, options);
        }

        public LlamactlDb CreateDbContext() => new(options);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}