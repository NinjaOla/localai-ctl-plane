using llamactl.Agent;
using llamactl.Web.Features.Presets;

namespace llamactl.Tests.Nodes;

public sealed class PresetTests
{
    [Fact]
    public void Parser_reads_sections_and_rejects_unknown_flags()
    {
        var document = PresetDocument.Parse("""
            [*]
            ctx-size = 8192

            [model-a]
            unknown-flag = true
            """);

        var errors = document.Validate(new HashSet<string>(["ctx-size"], StringComparer.Ordinal));

        Assert.Equal(["[model-a] option 'unknown-flag' is not supported by this node's llama.cpp build."], errors);
    }

    [Fact]
    public void Parser_rejects_duplicate_keys_and_diff_marks_changes()
    {
        Assert.Throws<FormatException>(() => PresetDocument.Parse("[*]\nctx-size=1\nctx-size=2"));

        var diff = PresetDiff.Create("[*]\nctx-size=1", "[*]\nctx-size=2");

        Assert.Contains("- ctx-size=1", diff);
        Assert.Contains("+ ctx-size=2", diff);
    }

    [Fact]
    public async Task Agent_preset_service_reads_and_atomically_replaces_configured_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"llamactl-preset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = NodeConfigurationTests.CreateConfiguration() with
            {
                Paths = NodeConfigurationTests.CreateConfiguration().Paths with
                {
                    PresetFile = Path.Combine(root, "models.ini")
                }
            };
            var state = new NodeRuntimeState { Configuration = configuration };
            var service = new PresetFileService(state);

            await service.WriteAsync("[*]\nctx-size=8192", CancellationToken.None);
            var restored = await service.ReadAsync(CancellationToken.None);

            Assert.Equal("[*]\nctx-size=8192", restored);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}