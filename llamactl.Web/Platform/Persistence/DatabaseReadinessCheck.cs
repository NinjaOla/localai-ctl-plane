using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace llamactl.Web.Platform.Persistence;

internal sealed class DatabaseReadinessCheck(IDbContextFactory<LlamactlDb> dbFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("SQLite database is available.")
                : HealthCheckResult.Unhealthy("SQLite database is unavailable.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbUpdateException)
        {
            return HealthCheckResult.Unhealthy("SQLite database readiness check failed.", exception);
        }
    }
}