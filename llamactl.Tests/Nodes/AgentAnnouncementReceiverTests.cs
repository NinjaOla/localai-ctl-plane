using llamactl.Contracts;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace llamactl.Tests.Nodes;

public sealed class AgentAnnouncementReceiverTests
{
    [Fact]
    public async Task Announcement_updates_registered_node_and_retains_full_payload()
    {
        var nodeId = Guid.NewGuid();
        await using var fixture = await DatabaseFixture.CreateAsync(nodeId);
        var receiver = new AgentAnnouncementReceiver(fixture);

        await receiver.ReceiveAsync(
            nodeId,
            CreateEnvelope(nodeId, Protocol.SchemaVersion),
            CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var stored = await db.Nodes.SingleAsync();
        Assert.NotNull(stored.LastSeen);
        Assert.Equal("AMD Radeon 8060S", stored.GpuName);
        Assert.Equal(98_304, stored.VramTotalMiB);
        Assert.Equal("b8342-runtime", stored.LlamaCppVersion);
        Assert.Equal("7.2.1", stored.RocmVersion);
        Assert.Contains("modelsRoot", stored.AnnouncementJson);
        Assert.Equal(1, stored.Version);
    }

    [Theory]
    [InlineData(true, Protocol.SchemaVersion)]
    [InlineData(false, Protocol.SchemaVersion + 1)]
    public async Task Announcement_rejects_spoofed_identity_or_unsupported_schema(
        bool spoofIdentity,
        int schemaVersion)
    {
        var authenticatedNodeId = Guid.NewGuid();
        await using var fixture = await DatabaseFixture.CreateAsync(authenticatedNodeId);
        var receiver = new AgentAnnouncementReceiver(fixture);
        var envelopeNodeId = spoofIdentity ? Guid.NewGuid() : authenticatedNodeId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.ReceiveAsync(
            authenticatedNodeId,
            CreateEnvelope(envelopeNodeId, schemaVersion),
            CancellationToken.None));
    }

    private static Envelope<AgentAnnouncement> CreateEnvelope(Guid nodeId, int schemaVersion) =>
        new(
            Guid.NewGuid(),
            null,
            nodeId,
            schemaVersion,
            DateTimeOffset.UtcNow,
            new AgentAnnouncement(
                new NodeDescription(
                    "node-01",
                    "Ubuntu 24.04",
                    "6.8.0",
                    "AMD Radeon 8060S",
                    98_304,
                    "7.2.1",
                    "b8342-description",
                    [new MountedFileSystem("/models", 2_000, 1_500, true)]),
                [new PathProposal("modelsRoot", "/models", "Existing writable volume.")],
                [new RuntimeDescriptor(
                    RuntimeId.LlamaCpp,
                    "llama.cpp",
                    "b8342-runtime",
                    "/opt/llama.cpp/build/bin",
                    true,
                    ConfigFormat.LlamaCppIni,
                    new HashSet<ModelFormat> { ModelFormat.Gguf },
                    RuntimeCapabilities.MultiModelRouting,
                    new Dictionary<string, string> { ["models-dir"] = "--models-dir DIR" })]));

    private sealed class DatabaseFixture(
        SqliteConnection connection,
        DbContextOptions<LlamactlDb> options)
        : IDbContextFactory<LlamactlDb>, IAsyncDisposable
    {
        public static async Task<DatabaseFixture> CreateAsync(Guid nodeId)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LlamactlDb>()
                .UseSqlite(connection)
                .Options;

            await using var db = new LlamactlDb(options);
            await db.Database.EnsureCreatedAsync();
            db.Nodes.Add(new NodeRecord
            {
                Id = nodeId,
                Name = "node-01",
                BootstrapTokenHash = BootstrapToken.Hash("bootstrap-secret"),
            });
            await db.SaveChangesAsync();

            return new DatabaseFixture(connection, options);
        }

        public LlamactlDb CreateDbContext() => new(options);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}