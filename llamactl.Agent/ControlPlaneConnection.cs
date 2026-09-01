using llamactl.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace llamactl.Agent;

internal sealed class ControlPlaneConnection(
    IOptions<AgentBootstrapOptions> options,
    SystemNodeDiscovery discovery,
    DesiredStateReconciler reconciler,
    InstanceLogBuffer logs,
    PresetFileService presetFiles,
    ModelFileService modelFiles,
    ModelDownloadManager modelDownloads,
    ILogger<ControlPlaneConnection> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrap = options.Value;
        var hubUrl = new Uri(new Uri(bootstrap.ControlPlaneUrl), Protocol.AgentHubPath);
        using var retryTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var connection = CreateConnection(hubUrl, bootstrap);
            var disconnected = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += _ =>
            {
                disconnected.TrySetResult();
                return Task.CompletedTask;
            };
            var desiredStateChanges = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            connection.On<long>("DesiredStateChanged", version => desiredStateChanges.Writer.TryWrite(version));
            connection.On<string>("ReadPreset", () => presetFiles.ReadAsync(stoppingToken));
            connection.On<string, bool>("WritePreset", async content =>
            {
                await presetFiles.WriteAsync(content, stoppingToken);
                return true;
            });
            connection.On<ModelInventory>("ScanModels", () => modelFiles.ScanAsync(stoppingToken));
            connection.On<bool, ReconcileLibraryResult>("ReconcileFlatDir", dryRun => modelFiles.ReconcileAsync(dryRun, stoppingToken));
            connection.On<InspectGgufRequest, GgufInspection>("InspectGguf", request => modelFiles.InspectAsync(request, stoppingToken));
            connection.On<DeleteModelRequest, DeleteModelResult>("DeleteModel", request => modelFiles.DeleteAsync(request, stoppingToken));
            connection.On<StartModelDownload, Guid>("StartDownload", request => Task.FromResult(modelDownloads.Start(request)));
            connection.On<Guid, bool>("CancelDownload", id => Task.FromResult(modelDownloads.Cancel(id)));

            try
            {
                await connection.StartAsync(stoppingToken);
                logger.LogInformation("Connected to control plane as node {NodeId}", bootstrap.NodeId);
                var announcement = await discovery.DiscoverAsync(stoppingToken);
                await connection.InvokeAsync(
                    "Announce",
                    new Envelope<AgentAnnouncement>(
                        Guid.NewGuid(),
                        null,
                        bootstrap.NodeId,
                        Protocol.SchemaVersion,
                        DateTimeOffset.UtcNow,
                        announcement),
                    stoppingToken);
                await RunConnectedAsync(
                    connection,
                    disconnected.Task,
                    desiredStateChanges,
                    bootstrap.NodeId,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Control-plane connection failed; retrying");
            }

            if (!await retryTimer.WaitForNextTickAsync(stoppingToken))
                return;
        }
    }

    private async Task RunConnectedAsync(
        HubConnection connection,
        Task disconnected,
        Channel<long> desiredStateChanges,
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        var state = new ReconciliationState(await ReconcileAsync(connection, cancellationToken));
        using var connected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = SendHeartbeatsAsync(connection, nodeId, state, connected.Token);
        var reconciliationTask = ReconcileChangesAsync(
            connection,
            desiredStateChanges.Reader,
            state,
            connected.Token);
        var periodicTask = QueuePeriodicReconciliationAsync(desiredStateChanges.Writer, connected.Token);
        var logTask = PublishLogsAsync(connection, connected.Token);
        var downloadTask = PublishDownloadProgressAsync(connection, connected.Token);
        var completed = await Task.WhenAny(disconnected, heartbeatTask, reconciliationTask, periodicTask, logTask, downloadTask);
        connected.Cancel();
        if (completed != disconnected)
            await completed;
    }

    private async Task PublishDownloadProgressAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        await foreach (var batch in modelDownloads.ReadProgressAsync(cancellationToken))
            await connection.InvokeAsync("PublishDownloadProgress", batch, cancellationToken);
    }

    private async Task PublishLogsAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        await foreach (var batch in logs.ReadBatchesAsync(cancellationToken))
            await connection.InvokeAsync("PublishLogs", batch, cancellationToken);
    }

    private static async Task SendHeartbeatsAsync(
        HubConnection connection,
        Guid nodeId,
        ReconciliationState state,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            var report = state.Get();
            await connection.InvokeAsync(
                "Heartbeat",
                new Envelope<AgentHeartbeat>(
                    Guid.NewGuid(),
                    null,
                    nodeId,
                    Protocol.SchemaVersion,
                    DateTimeOffset.UtcNow,
                    new AgentHeartbeat(report.ValidationIssues)),
                cancellationToken);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task ReconcileChangesAsync(
        HubConnection connection,
        ChannelReader<long> desiredStateChanges,
        ReconciliationState state,
        CancellationToken cancellationToken)
    {
        await foreach (var _ in desiredStateChanges.ReadAllAsync(cancellationToken))
        {
            var report = await ReconcileAsync(connection, cancellationToken);
            state.Set(report);
        }
    }

    private static async Task QueuePeriodicReconciliationAsync(
        ChannelWriter<long> desiredStateChanges,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            desiredStateChanges.TryWrite(-1);
    }

    private async Task<ReconciliationReport> ReconcileAsync(
        HubConnection connection,
        CancellationToken cancellationToken)
    {
        var desiredState = await connection.InvokeAsync<AgentDesiredState>(
            "GetDesiredState",
            cancellationToken);
        var report = await reconciler.ReconcileAsync(desiredState, cancellationToken);
        await connection.InvokeAsync("ReportReconciliation", report, cancellationToken);
        return report;
    }

    private sealed class ReconciliationState(ReconciliationReport current)
    {
        public ReconciliationReport Get() => Volatile.Read(ref current);
        public void Set(ReconciliationReport report) => Volatile.Write(ref current, report);
    }

    private static HubConnection CreateConnection(Uri hubUrl, AgentBootstrapOptions bootstrap) =>
        new HubConnectionBuilder()
            .WithUrl(hubUrl, httpConnectionOptions =>
            {
                httpConnectionOptions.Headers.Add(Protocol.NodeIdHeader, bootstrap.NodeId.ToString());
                httpConnectionOptions.Headers.Add(Protocol.BootstrapTokenHeader, bootstrap.BootstrapToken);
            })
            .WithAutomaticReconnect()
            .Build();
}