using System.Text;
using llamactl.Agent;
using llamactl.Contracts;
using llamactl.Web.Features.Models;
using llamactl.Web.Platform.NodeGateway;

namespace llamactl.Tests.Nodes;

public sealed class ModelManagementTests
{
    [Fact]
    public void Library_plan_groups_shards_excludes_drafts_and_removes_stale_entries()
    {
        using var fixture = ModelFixture.Create();
        var stale = Path.Combine(fixture.Configuration.Paths.FlatDir, "stale.gguf");
        File.WriteAllText(stale, "stale");
        var files = new[]
        {
            new ModelFile("repo/model-Q4_K_M-00001-of-00002.gguf", 10, true, false, false),
            new ModelFile("repo/model-Q4_K_M-00002-of-00002.gguf", 10, true, false, false),
            new ModelFile("repo/mmproj-model-F16.gguf", 4, false, true, false),
            new ModelFile("repo/model-dflash.gguf", 5, false, false, true),
        };

        var operations = ModelFileService.PlanLibrary(fixture.Configuration, files);

        Assert.NotEqual(ModelFileService.ModelGroup(files[0]), ModelFileService.ModelGroup(files[2]));
        Assert.Equal(ModelFileService.ModelFamily(files[0]), ModelFileService.ModelFamily(files[2]));
        Assert.Contains(operations, operation => operation.Kind == LibraryOperationKind.Remove && operation.Path == stale);
        var links = operations.Where(operation => operation.Kind == LibraryOperationKind.CreateLink).ToList();
        Assert.Equal(3, links.Count);
        Assert.Single(links.Select(link => Path.GetDirectoryName(link.Path)).Distinct());
        Assert.DoesNotContain(links, link => link.Path.Contains("dflash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scan_excludes_blob_storage_and_reports_unreferenced_blob()
    {
        using var fixture = ModelFixture.Create();
        var snapshot = Path.Combine(fixture.Configuration.Paths.HfHome, "hub", "models--org-repo", "snapshots", "main");
        var blobs = Path.Combine(fixture.Configuration.Paths.HfHome, "hub", "models--org-repo", "blobs");
        Directory.CreateDirectory(snapshot);
        Directory.CreateDirectory(blobs);
        File.WriteAllText(Path.Combine(snapshot, "model.gguf"), "model");
        File.WriteAllText(Path.Combine(blobs, "orphan"), "blob");

        var inventory = await fixture.Service.ScanAsync(CancellationToken.None);

        Assert.Single(inventory.Files);
        Assert.Single(inventory.OrphanedBlobs);
        Assert.DoesNotContain(inventory.Files, file => file.RelativePath.Contains("blobs"));
    }

    [Fact]
    public async Task Gguf_inspection_reads_architecture_context_and_draft_tensor()
    {
        using var fixture = ModelFixture.Create();
        var path = Path.Combine(fixture.Configuration.Paths.ModelsRoot, "draft.gguf");
        WriteGguf(path);

        var inspection = await fixture.Service.InspectAsync(new("draft.gguf"), CancellationToken.None);

        Assert.Equal("llama", inspection.Architecture);
        Assert.Equal(131_072, inspection.TrainingContext);
        Assert.True(inspection.HasDraftHead);
        Assert.Equal(["blk.0.nextn.weight"], inspection.DraftTensors);
    }

    [Fact]
    public async Task Delete_rejects_paths_outside_configured_root()
    {
        using var fixture = ModelFixture.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteAsync(new(["../outside.gguf"]), CancellationToken.None));
    }

    [Fact]
    public void Fit_estimate_sums_shards_and_prioritizes_disk_failure()
    {
        var files = new[]
        {
            new HuggingFaceFile("model-Q4_K_M-00001-of-00002.gguf", 60 * 1024 * 1024),
            new HuggingFaceFile("model-Q4_K_M-00002-of-00002.gguf", 60 * 1024 * 1024),
        };

        var diskFailure = Assert.Single(ModelFitCalculator.Estimate(files, vramMiB: 100, diskFreeBytes: 110 * 1024 * 1024));
        var vramFailure = Assert.Single(ModelFitCalculator.Estimate(files, vramMiB: 100, diskFreeBytes: 200 * 1024 * 1024));

        Assert.Equal(120 * 1024 * 1024, diskFailure.SizeBytes);
        Assert.Equal(FitVerdict.ExceedsDisk, diskFailure.Verdict);
        Assert.Equal(FitVerdict.ExceedsVram, vramFailure.Verdict);
        Assert.Equal(ModelFitCalculator.Variant(files[0]), ModelFitCalculator.Variant(files[1]));
    }

    [Fact]
    public void Download_progress_is_bounded_and_scoped_to_node()
    {
        var firstNode = Guid.NewGuid();
        var secondNode = Guid.NewGuid();
        var store = new DownloadProgressStore();
        for (var index = 0; index < DownloadProgressStore.Capacity + 5; index++)
        {
            var id = Guid.NewGuid();
            store.Update(firstNode, [new(id, DownloadState.Completed, index, index, null, null)]);
        }
        var secondDownload = new DownloadProgress(Guid.NewGuid(), DownloadState.Running, 1, 2, "model.gguf", null);
        store.Update(secondNode, [secondDownload]);

        Assert.True(store.Read(firstNode).Count < DownloadProgressStore.Capacity);
        Assert.Equal([secondDownload], store.Read(secondNode));
    }

    private static void WriteGguf(string path)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
        writer.Write(0x46554747u);
        writer.Write(3u);
        writer.Write(1ul);
        writer.Write(2ul);
        WriteString(writer, "general.architecture"); writer.Write(8u); WriteString(writer, "llama");
        WriteString(writer, "llama.context_length"); writer.Write(10u); writer.Write(131_072ul);
        WriteString(writer, "blk.0.nextn.weight"); writer.Write(1u); writer.Write(1ul); writer.Write(0u); writer.Write(0ul);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    private sealed class ModelFixture : IDisposable
    {
        private readonly string root;
        private ModelFixture(string root, NodeConfiguration configuration)
        {
            this.root = root;
            Configuration = configuration;
            var state = new NodeRuntimeState { Configuration = configuration };
            Service = new ModelFileService(state, TimeProvider.System);
        }
        public NodeConfiguration Configuration { get; }
        public ModelFileService Service { get; }
        public static ModelFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"llamactl-models-{Guid.NewGuid():N}");
            var models = Directory.CreateDirectory(Path.Combine(root, "models")).FullName;
            var hf = Directory.CreateDirectory(Path.Combine(root, "hf")).FullName;
            var flat = Directory.CreateDirectory(Path.Combine(root, "flat")).FullName;
            var paths = NodeConfigurationTests.CreateConfiguration().Paths with { ModelsRoot = models, HfHome = hf, FlatDir = flat };
            return new(root, NodeConfigurationTests.CreateConfiguration() with { Paths = paths });
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}