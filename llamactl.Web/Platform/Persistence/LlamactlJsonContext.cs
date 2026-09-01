using System.Text.Json.Serialization;
using llamactl.Contracts;

namespace llamactl.Web.Platform.Persistence;

[JsonSerializable(typeof(AgentAnnouncement))]
[JsonSerializable(typeof(NodeConfiguration))]
[JsonSerializable(typeof(IReadOnlyList<ValidationIssue>))]
[JsonSerializable(typeof(InstanceSpec))]
internal sealed partial class LlamactlJsonContext : JsonSerializerContext;