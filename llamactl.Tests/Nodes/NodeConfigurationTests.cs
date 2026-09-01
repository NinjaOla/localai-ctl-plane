using llamactl.Contracts;
using llamactl.Web.Features.Nodes.Configure;

namespace llamactl.Tests.Nodes;

public sealed class NodeConfigurationTests
{
    [Fact]
    public void Configuration_rejects_reversed_port_range()
    {
        var configuration = CreateConfiguration() with { PortRange = new(49_000, 48_000) };

        var error = ConfigureNode.Validate(configuration);

        Assert.Contains("Port range", error);
    }

    internal static NodeConfiguration CreateConfiguration() => new(
        new NodePaths("/bin", "/source", "/rocm", "/models", "/hf", "/flat",
            "/models/models.ini", "/empty", "/systemd", "/config"),
        98_304,
        new PortRange(48_000, 48_999),
        99,
        true);
}