using llamactl.Contracts;

namespace llamactl.Web.Platform.Persistence;

public sealed class InstanceRecord
{
    public Guid Id { get; init; }
    public Guid NodeId { get; init; }
    public required string Name { get; set; }
    public required string SpecJson { get; set; }
    public DesiredInstanceState DesiredState { get; set; }
    public ObservedInstanceState ObservedState { get; set; } = ObservedInstanceState.Unknown;
    public int? ProcessId { get; set; }
    public int? AdoptProcessId { get; set; }
    public string? Error { get; set; }
    public long Revision { get; set; }
}