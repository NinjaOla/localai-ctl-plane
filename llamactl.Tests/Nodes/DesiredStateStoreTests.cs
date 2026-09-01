using System.Text.Json;
using llamactl.Contracts;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Tests.Nodes;

public sealed class DesiredStateStoreTests
{
    [Fact]
    public async Task Desired_state_round_trips_configuration_and_instances()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var store = new DesiredStateStore(fixture);

        var state = await store.GetAsync(fixture.NodeId, CancellationToken.None);

        Assert.Equal(4, state.Version);
        Assert.Equal(98_304, state.Configuration?.VramBudgetMiB);
        var instance = Assert.Single(state.Instances);
        Assert.Equal("router-01", instance.Spec.Name);
        Assert.Equal(DesiredInstanceState.Running, instance.State);
    }

    [Fact]
    public async Task Only_current_reconciliation_report_updates_observed_state()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var store = new DesiredStateStore(fixture);
        var failed = new ObservedInstance(fixture.InstanceId, 1, ObservedInstanceState.Failed, null, "old");

        await store.ReportAsync(fixture.NodeId, new(3, [], [failed]), CancellationToken.None);
        await using (var staleDb = fixture.CreateDbContext())
            Assert.Equal(ObservedInstanceState.Unknown, (await staleDb.Instances.SingleAsync()).ObservedState);

        var running = failed with { State = ObservedInstanceState.Running, ProcessId = 1234, Error = null };
        await store.ReportAsync(fixture.NodeId, new(4, [], [running]), CancellationToken.None);
        await using var currentDb = fixture.CreateDbContext();
        var stored = await currentDb.Instances.SingleAsync();
        Assert.Equal(ObservedInstanceState.Running, stored.ObservedState);
        Assert.Equal(1234, stored.ProcessId);
    }

    private sealed class DatabaseFixture(
        SqliteConnection connection,
        DbContextOptions<LlamactlDb> options,
        Guid nodeId,
        Guid instanceId) : IDbContextFactory<LlamactlDb>, IAsyncDisposable
    {
        public Guid NodeId => nodeId;
        public Guid InstanceId => instanceId;

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LlamactlDb>().UseSqlite(connection).Options;
            var nodeId = Guid.NewGuid();
            var instanceId = Guid.NewGuid();
            var configuration = NodeConfigurationTests.CreateConfiguration();
            var spec = new InstanceSpec("router-01", RuntimeId.LlamaCpp, InstanceKind.Managed,
                "router", null, null, 48_000, new Dictionary<string, string?>(), true);
            await using var db = new LlamactlDb(options);
            await db.Database.EnsureCreatedAsync();
            db.Nodes.Add(new NodeRecord
            {
                Id = nodeId,
                Name = "node-01",
                BootstrapTokenHash = BootstrapToken.Hash("secret"),
                ConfigurationJson = JsonSerializer.Serialize(configuration, LlamactlJsonContext.Default.NodeConfiguration),
                DesiredStateVersion = 4,
            });
            db.Instances.Add(new InstanceRecord
            {
                Id = instanceId,
                NodeId = nodeId,
                Name = spec.Name,
                SpecJson = JsonSerializer.Serialize(spec, LlamactlJsonContext.Default.InstanceSpec),
                DesiredState = DesiredInstanceState.Running,
                Revision = 1,
            });
            await db.SaveChangesAsync();
            return new(connection, options, nodeId, instanceId);
        }

        public LlamactlDb CreateDbContext() => new(options);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}