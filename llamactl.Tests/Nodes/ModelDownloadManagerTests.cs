using System.Net;
using System.Net.Http.Headers;
using llamactl.Agent;
using llamactl.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace llamactl.Tests.Nodes;

public sealed class ModelDownloadManagerTests
{
    [Fact]
    public async Task Download_writes_hf_blob_snapshot_link_and_completed_progress()
    {
        using var fixture = DownloadFixture.Create(new ByteHandler("model-data"));
        var request = new StartModelDownload("org/repo", "main", [new HuggingFaceFile("model.gguf", 10)]);

        var id = fixture.Manager.Start(request);
        var terminal = await ReadTerminalAsync(fixture.Manager, id);

        Assert.True(terminal.State == DownloadState.Completed, terminal.Error);
        var repository = Path.Combine(fixture.HfHome, "hub", "models--org--repo");
        Assert.Single(Directory.EnumerateFiles(Path.Combine(repository, "blobs")));
        var snapshot = Path.Combine(repository, "snapshots", "main", "model.gguf");
        Assert.NotNull(new FileInfo(snapshot).LinkTarget);
        Assert.Equal("model-data", await File.ReadAllTextAsync(snapshot));
        var flatLink = Assert.Single(Directory.EnumerateFiles(fixture.FlatDirectory));

        var deleted = await fixture.Files.DeleteAsync(
            new([Path.GetRelativePath(fixture.HfHome, snapshot)], FromHfCache: true),
            CancellationToken.None);

        Assert.Equal(10, deleted.FreedBytes);
        Assert.False(File.Exists(snapshot));
        Assert.False(File.Exists(flatLink));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(repository, "blobs")));
    }

    [Fact]
    public async Task Cancellation_reports_cancelled_and_removes_partial_files()
    {
        using var fixture = DownloadFixture.Create(new BlockingHandler());
        var id = fixture.Manager.Start(new("org/repo", "main", [new HuggingFaceFile("model.gguf", 10)]));

        Assert.True(fixture.Manager.Cancel(id));
        var terminal = await ReadTerminalAsync(fixture.Manager, id);

        Assert.Equal(DownloadState.Cancelled, terminal.State);
        Assert.Empty(Directory.EnumerateFiles(fixture.HfHome, "*.partial", SearchOption.AllDirectories));
    }

    private static async Task<DownloadProgress> ReadTerminalAsync(ModelDownloadManager manager, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var batch in manager.ReadProgressAsync(timeout.Token))
        {
            var terminal = batch.LastOrDefault(item => item.Id == id && item.State is DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed);
            if (terminal is not null) return terminal;
        }
        throw new TimeoutException("No terminal download progress was reported.");
    }

    private sealed class DownloadFixture(string root, string hfHome, string flatDirectory, ModelFileService files, ModelDownloadManager manager) : IDisposable
    {
        public string HfHome => hfHome;
        public string FlatDirectory => flatDirectory;
        public ModelFileService Files => files;
        public ModelDownloadManager Manager => manager;
        public static DownloadFixture Create(HttpMessageHandler handler)
        {
            var root = Path.Combine(Path.GetTempPath(), $"llamactl-download-{Guid.NewGuid():N}");
            var models = Directory.CreateDirectory(Path.Combine(root, "models")).FullName;
            var hf = Directory.CreateDirectory(Path.Combine(root, "hf")).FullName;
            var flat = Directory.CreateDirectory(Path.Combine(root, "flat")).FullName;
            var configuration = NodeConfigurationTests.CreateConfiguration() with
            {
                Paths = NodeConfigurationTests.CreateConfiguration().Paths with { ModelsRoot = models, HfHome = hf, FlatDir = flat }
            };
            var state = new NodeRuntimeState { Configuration = configuration };
            var files = new ModelFileService(state, TimeProvider.System);
            var factory = new TestHttpClientFactory(new HttpClient(handler));
            return new(root, hf, flat, files, new(factory, state, files, NullLogger<ModelDownloadManager>.Instance));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ByteHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
            response.Headers.ETag = new EntityTagHeaderValue("\"blobhash\"");
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
    }
}