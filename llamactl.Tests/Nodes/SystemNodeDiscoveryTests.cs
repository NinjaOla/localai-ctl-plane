using llamactl.Agent;

namespace llamactl.Tests.Nodes;

public sealed class SystemNodeDiscoveryTests
{
    [Fact]
    public void Rocm_info_reports_marketing_name_and_global_memory()
    {
        const string output = """
            Agent 1
            Marketing Name:          AMD Ryzen AI MAX+ PRO 395
            Feature:                 None
            Global Memory Size:      137438953472 bytes
            Agent 2
            Name:                    gfx1151
            Marketing Name:          AMD Radeon 8060S
            Feature:                 KERNEL_DISPATCH
            Global Memory Size:      103079215104 bytes
            """;

        var (gpuName, vramTotalMiB) = SystemNodeDiscovery.ParseRocmInfo(output);

        Assert.Equal("AMD Radeon 8060S", gpuName);
        Assert.Equal(98_304, vramTotalMiB);
    }

    [Fact]
    public void Llama_help_is_parsed_into_flag_schema()
    {
        const string help = """
            -m, --model FNAME              model path
            --models-dir DIR               directory of models
            --spec-type TYPE               speculative decoding mode
            --mmproj FILE                  multimodal projector
            """;

        var flags = SystemNodeDiscovery.ParseFlagSchema(help);

        Assert.Equal(4, flags.Count);
        Assert.Contains("model", flags.Keys);
        Assert.Contains("models-dir", flags.Keys);
        Assert.Contains("spec-type", flags.Keys);
        Assert.Contains("mmproj", flags.Keys);
    }
}