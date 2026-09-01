using llamactl.Contracts;

namespace llamactl.Agent;

internal sealed class NodeRuntimeState
{
    private NodeConfiguration? configuration;
    public NodeConfiguration? Configuration
    {
        get => Volatile.Read(ref configuration);
        set => Volatile.Write(ref configuration, value);
    }
}