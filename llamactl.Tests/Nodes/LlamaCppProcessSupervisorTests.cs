using llamactl.Agent;
using llamactl.Contracts;

namespace llamactl.Tests.Nodes;

public sealed class LlamaCppProcessSupervisorTests
{
    [Fact]
    public void Router_start_info_uses_configured_paths_port_defaults_and_custom_args()
    {
        var configuration = NodeConfigurationTests.CreateConfiguration();
        var spec = new InstanceSpec(
            "router-01", RuntimeId.LlamaCpp, InstanceKind.Managed, "router", null, null, 48_010,
            new Dictionary<string, string?> { ["ctx-size"] = "8192", ["metrics"] = null }, true);

        var startInfo = LlamaCppProcessSupervisor.BuildStartInfo(configuration, spec);

        Assert.Equal(OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server", Path.GetFileName(startInfo.FileName));
        Assert.Equal("/hf", startInfo.Environment["HF_HOME"]);
        Assert.Equal("/empty", startInfo.Environment["LLAMA_CACHE"]);
        Assert.Equal([
            "--models-dir", "/flat", "--models-preset", "/models/models.ini",
            "--port", "48010", "--n-gpu-layers", "99", "--jinja",
            "--ctx-size", "8192", "--metrics"
        ], startInfo.ArgumentList);
    }
}