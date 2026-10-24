using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Readiness probe: proves the process can actually reach PostgreSQL and that the schema is
/// present, not merely that the connection string parses.
/// </summary>
/// <remarks>
/// The check resolves the context from its own scope because <see cref="ConvencionesDbContext"/> is
/// scoped while health checks are resolved from the root provider.
/// </remarks>
public sealed class DatabaseReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseReadinessHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConvencionesDbContext>();

        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
            }

            // CanConnectAsync is satisfied by any reachable database; confirm the schema this
            // application needs is actually deployed.
            var schemaPresent = await db.Database
                .SqlQuery<bool>($"select exists (select 1 from information_schema.schemata where schema_name = 'app') as \"Value\"")
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);

            return schemaPresent
                ? HealthCheckResult.Healthy("PostgreSQL reachable and the 'app' schema is present.")
                : HealthCheckResult.Unhealthy("PostgreSQL reachable but the 'app' schema is missing; migrations have not been applied.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Database readiness check failed.");
            return HealthCheckResult.Unhealthy("Database readiness check failed.", ex);
        }
    }
}
