using System.ComponentModel.DataAnnotations;

namespace llamactl.Agent;

public sealed class AgentBootstrapOptions
{
    public const string SectionName = "Llamactl";

    [Required]
    [Url]
    public required string ControlPlaneUrl { get; init; }

    public required Guid NodeId { get; init; }

    [Required]
    public required string BootstrapToken { get; init; }
}