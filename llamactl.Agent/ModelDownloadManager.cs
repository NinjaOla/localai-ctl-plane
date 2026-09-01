using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using llamactl.Contracts;

namespace llamactl.Agent;

internal sealed class ModelDownloadManager(IHttpClientFactory httpClientFactory, NodeRuntimeState runtimeState, ModelFileService modelFiles, ILogger<ModelDownloadManager> logger)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> downloads = new();
    private readonly Channel<DownloadProgress> progress = Channel.CreateBounded<DownloadProgress>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public Guid Start(StartModelDownload request)
    {
        if (request.Files.Count == 0) throw new InvalidOperationException("Select at least one file.");
        var id = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        if (!downloads.TryAdd(id, cancellation)) throw new InvalidOperationException("Could not create download.");
        _ = RunAsync(id, request, cancellation.Token);
        return id;
    }

    public bool Cancel(Guid id) { if (!downloads.TryGetValue(id, out var cancellation)) return false; cancellation.Cancel(); return true; }

    public async IAsyncEnumerable<IReadOnlyList<DownloadProgress>> ReadProgressAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await progress.Reader.WaitToReadAsync(cancellationToken))
        {
            var batch = new List<DownloadProgress>(32);
            while (batch.Count < 32 && progress.Reader.TryRead(out var update)) batch.Add(update);
            if (batch.Count > 0) yield return batch;
        }
    }

    private async Task RunAsync(Guid id, StartModelDownload request, CancellationToken cancellationToken)
    {
        var total = request.Files.Sum(file => file.SizeBytes);
        long received = 0;
        Publish(id, DownloadState.Running, received, total, null, null);
        try
        {
            var configuration = runtimeState.Configuration ?? throw new InvalidOperationException("Node configuration has not been applied.");
            var repositoryDirectory = Path.Combine(configuration.Paths.HfHome, "hub", $"models--{request.Repository.Replace("/", "--", StringComparison.Ordinal)}", "snapshots", Sanitize(request.Revision));
            var client = httpClientFactory.CreateClient(nameof(ModelDownloadManager));
            foreach (var file in request.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveInside(repositoryDirectory, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? partial = null;
                try
                {
                    var url = $"https://huggingface.co/{request.Repository}/resolve/{Uri.EscapeDataString(request.Revision)}/{string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString))}";
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var repositoryRoot = Directory.GetParent(Directory.GetParent(repositoryDirectory)!.FullName)!.FullName;
                    var blobName = response.Headers.ETag?.Tag.Trim('"').TrimStart("W/".ToCharArray()) ?? Guid.NewGuid().ToString("N");
                    var blob = ResolveInside(Path.Combine(repositoryRoot, "blobs"), blobName);
                    Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
                    partial = blob + ".partial";
                    {
                        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
                        var buffer = new byte[1024 * 1024];
                        int count;
                        long lastReported = 0;
                        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                            received += count;
                            if (received - lastReported >= 4 * 1024 * 1024) { Publish(id, DownloadState.Running, received, total, file.Path, null); lastReported = received; }
                        }
                    }
                    File.Move(partial, blob, overwrite: true);
                    partial = null;
                    if (File.Exists(destination)) File.Delete(destination);
                    File.CreateSymbolicLink(destination, Path.GetRelativePath(Path.GetDirectoryName(destination)!, blob));
                }
                finally { if (partial is not null && File.Exists(partial)) File.Delete(partial); }
            }
            await modelFiles.ReconcileAsync(dryRun: false, cancellationToken);
            Publish(id, DownloadState.Completed, received, total, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Publish(id, DownloadState.Cancelled, received, total, null, null); }
        catch (Exception exception) { logger.LogError(exception, "Model download {DownloadId} failed", id); Publish(id, DownloadState.Failed, received, total, null, exception.Message); }
        finally { if (downloads.TryRemove(id, out var cancellation)) cancellation.Dispose(); }
    }

    private void Publish(Guid id, DownloadState state, long received, long total, string? file, string? error) => progress.Writer.TryWrite(new(id, state, received, total, file, error));
    private static string ResolveInside(string root, string relativePath) { var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar; var path = Path.GetFullPath(Path.Combine(root, relativePath)); if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Download path escapes the repository directory."); return path; }
    private static string Sanitize(string value) => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}