using llamactl.Contracts;

namespace llamactl.Agent;

internal sealed class DesiredStateReconciler(
    NodeConfigurationApplier configurationApplier,
    LlamaCppProcessSupervisor supervisor,
    NodeRuntimeState runtimeState)
{
    public async Task<ReconciliationReport> ReconcileAsync(
        AgentDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        var issues = configurationApplier.Apply(desiredState.Configuration);
        if (desiredState.Configuration is null
            || issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            return new(desiredState.Version, issues, desiredState.Instances.Select(instance =>
                new ObservedInstance(
                    instance.Id,
                    instance.Revision,
                    ObservedInstanceState.Failed,
                    null,
                    "Node configuration is invalid.")).ToList());
        }

        runtimeState.Configuration = desiredState.Configuration;
        var instances = await supervisor.ReconcileAsync(
            desiredState.Configuration,
            desiredState.Instances,
            cancellationToken);
        return new(desiredState.Version, issues, instances);
    }
}