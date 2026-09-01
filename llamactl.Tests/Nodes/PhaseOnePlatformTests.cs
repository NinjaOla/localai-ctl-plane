using llamactl.Contracts;
using llamactl.Web.Platform.Auth;
using llamactl.Web.Platform.NodeGateway;
using llamactl.Web.Platform.Persistence;
using llamactl.Web.Platform.Results;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace llamactl.Tests.Nodes;

public sealed class PhaseOnePlatformTests
{
    [Theory]
    [InlineData("same-secret-value", "same-secret-value", true)]
    [InlineData("same-secret-value", "different-secret", false)]
    public void Credential_comparison_matches_only_equal_values(string supplied, string expected, bool result) =>
        Assert.Equal(result, ApiKeyAuthenticationHandler.FixedTimeEquals(supplied, expected));

    [Fact]
    public async Task Guarded_invocation_converts_unexpected_failure_and_propagates_cancellation()
    {
        var failed = await Invoke.Guarded<string>(
            _ => ValueTask.FromException<Result<string>>(new IOException("disk")),
            NullLogger.Instance);
        Assert.Equal(ErrorKind.AgentError, failed.Error?.Kind);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Invoke.Guarded<string>(
            token => ValueTask.FromException<Result<string>>(new OperationCanceledException(token)),
            NullLogger.Instance,
            cancellation.Token));
    }

    [Fact]
    public async Task Readiness_reports_healthy_for_available_sqlite_database()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var check = new DatabaseReadinessCheck(fixture);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Log_receiver_rejects_instance_owned_by_another_node()
    {
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        await using var fixture = await DatabaseFixture.CreateAsync(ownerId, callerId, instanceId);
        var receiver = new AgentLogReceiver(fixture, new InstanceLogStore());
        var lines = new[] { new ProcessLogLine(instanceId, 1, DateTimeOffset.UtcNow, ProcessLogStream.StandardOutput, "line") };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiver.ReceiveAsync(callerId, lines, CancellationToken.None));
    }

    private sealed class DatabaseFixture(SqliteConnection connection, DbContextOptions<LlamactlDb> options)
        : IDbContextFactory<LlamactlDb>, IAsyncDisposable
    {
        public static async Task<DatabaseFixture> CreateAsync(Guid? ownerId = null, Guid? callerId = null, Guid? instanceId = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LlamactlDb>().UseSqlite(connection).Options;
            await using var db = new LlamactlDb(options);
            await db.Database.EnsureCreatedAsync();
            if (ownerId is { } owner && callerId is { } caller && instanceId is { } instance)
            {
                db.Nodes.AddRange(Node(owner, "owner"), Node(caller, "caller"));
                db.Instances.Add(new InstanceRecord
                {
                    Id = instance,
                    NodeId = owner,
                    Name = "router",
                    SpecJson = "{}",
                });
                await db.SaveChangesAsync();
            }
            return new(connection, options);
        }

        private static NodeRecord Node(Guid id, string name) => new()
        {
            Id = id,
            Name = name,
            BootstrapTokenHash = BootstrapToken.Hash("secret"),
        };

        public LlamactlDb CreateDbContext() => new(options);
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}