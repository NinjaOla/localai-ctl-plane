using System.Text.Json;
using llamactl.Contracts;
using llamactl.Web.Features.Instances;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Tests.Nodes;

public sealed class ManageInstanceTests
{
    [Fact]
    public async Task Lifecycle_persists_desired_state_and_restart_revision()
    {
        var nodeId = Guid.NewGuid();
        await using var fixture = await DatabaseFixture.CreateAsync(nodeId);
        var notifier = new RecordingNotifier();
        var handler = CreateHandler(fixture, notifier);
        var spec = CreateSpec("router-01", 48_000);

        var created = await handler.HandleAsync(
            new(InstanceAction.Upsert, nodeId, Spec: spec),
            CancellationToken.None);
        Assert.True(created.IsSuccess);
        var instanceId = Assert.IsType<Guid>(created.Value?.InstanceId);

        await handler.HandleAsync(new(InstanceAction.Start, nodeId, instanceId), CancellationToken.None);
        await handler.HandleAsync(new(InstanceAction.Restart, nodeId, instanceId), CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Instances.SingleAsync();
        Assert.Equal(DesiredInstanceState.Running, stored.DesiredState);
        Assert.Equal(2, stored.Revision);
        Assert.Equal(3, notifier.Notifications.Count);
        Assert.Equal([1L, 2L, 3L], notifier.Notifications.Select(item => item.Version));
    }

    [Fact]
    public async Task Instance_port_must_be_inside_node_range()
    {
        var nodeId = Guid.NewGuid();
        await using var fixture = await DatabaseFixture.CreateAsync(nodeId);
        var handler = CreateHandler(fixture, new RecordingNotifier());

        var result = await handler.HandleAsync(
            new(InstanceAction.Upsert, nodeId, Spec: CreateSpec("outside", 47_999)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Validation", result.Error?.Kind.ToString());
    }

    private static InstanceSpec CreateSpec(string name, int port) => new(
        name, RuntimeId.LlamaCpp, InstanceKind.Managed, "router", null, null, port,
        new Dictionary<string, string?>(), true);

    private static ManageInstance.Handler CreateHandler(
        IDbContextFactory<LlamactlDb> factory,
        IDesiredStateNotifier notifier)
    {
        var container = new ManageInstance(factory, notifier);
        return new(new ManageInstance.HandleBehavior(container));
    }

    private sealed class RecordingNotifier : IDesiredStateNotifier
    {
        public List<(Guid NodeId, long Version)> Notifications { get; } = [];
        public Task NotifyAsync(Guid nodeId, long version, CancellationToken cancellationToken)
        {
            Notifications.Add((nodeId, version));
            return Task.CompletedTask;
        }
    }

    private sealed class DatabaseFixture(
        SqliteConnection connection,
        DbContextOptions<LlamactlDb> options)
        : IDbContextFactory<LlamactlDb>, IAsyncDisposable
    {
        public static async Task<DatabaseFixture> CreateAsync(Guid nodeId)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LlamactlDb>().UseSqlite(connection).Options;
            await using var db = new LlamactlDb(options);
            await db.Database.EnsureCreatedAsync();
            db.Nodes.Add(new NodeRecord
            {
                Id = nodeId,
                Name = "node-01",
                BootstrapTokenHash = BootstrapToken.Hash("secret"),
                ConfigurationJson = JsonSerializer.Serialize(
                    NodeConfigurationTests.CreateConfiguration(),
                    LlamactlJsonContext.Default.NodeConfiguration),
            });
            await db.SaveChangesAsync();
            return new(connection, options);
        }

        public LlamactlDb CreateDbContext() => new(options);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}