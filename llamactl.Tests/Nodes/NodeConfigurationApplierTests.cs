using llamactl.Agent;
using llamactl.Contracts;

namespace llamactl.Tests.Nodes;

public sealed class NodeConfigurationApplierTests
{
    [Fact]
    public void Apply_creates_managed_directories_and_discovers_existing_setup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"llamactl-config-{Guid.NewGuid():N}");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var rocm = Directory.CreateDirectory(Path.Combine(root, "rocm")).FullName;
            var models = Directory.CreateDirectory(Path.Combine(root, "models")).FullName;
            var hf = Directory.CreateDirectory(Path.Combine(root, "hf")).FullName;
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server"), "test");
            File.WriteAllText(Path.Combine(models, "existing.gguf"), "test");
            var preset = Path.Combine(models, "models.ini");
            File.WriteAllText(preset, "[*]");
            var configuration = new NodeConfiguration(
                new NodePaths(bin, source, rocm, models, hf, Path.Combine(root, "flat"), preset,
                    Path.Combine(root, "empty"), Path.Combine(root, "systemd"), Path.Combine(root, "config")),
                98_304, new PortRange(48_000, 48_999), 99, true);

            var issues = new NodeConfigurationApplier().Apply(configuration);

            Assert.DoesNotContain(issues, issue => issue.Severity == ValidationSeverity.Error);
            Assert.Contains(issues, issue => issue.Code == "adoption.preset");
            Assert.Contains(issues, issue => issue.Code == "adoption.models");
            Assert.True(Directory.Exists(configuration.Paths.FlatDir));
            Assert.True(Directory.Exists(configuration.Paths.EmptyCache));
            Assert.True(Directory.Exists(configuration.Paths.ConfigRepo));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}