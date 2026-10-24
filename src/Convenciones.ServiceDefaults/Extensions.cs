using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Convenciones.ServiceDefaults;

/// <summary>
/// Cross-cutting host configuration shared by every service in the solution: service discovery,
/// resilience, OpenTelemetry and health probes.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Liveness and readiness are deliberately separate.
    /// <c>/health/live</c> answers "is this process running"; <c>/health/ready</c> answers
    /// "can it serve traffic", which includes a real database round-trip. A process that is alive
    /// but cannot reach PostgreSQL must be reported unready rather than accepting requests and
    /// failing them — the legacy system's failure mode was accepting requests it could not serve.
    /// </summary>
    public const string LivenessPath = "/health/live";

    public const string ReadinessPath = "/health/ready";

    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddDefaultHealthChecks();
        builder.ConfigureOpenTelemetry();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services
            .AddHealthChecks()
            // Self-check so the readiness pipeline is never empty, even before infrastructure
            // checks are registered.
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        // The OTLP exporter is only wired when an endpoint is configured, so a developer running
        // without a collector gets no exporter errors. Aspire sets this automatically.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Maps the health probes. Readiness is the aggregate of all registered checks; liveness
    /// excludes them, because a failing dependency must not cause an orchestrator to restart a
    /// healthy process.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks(LivenessPath, new HealthCheckOptions
        {
            Predicate = _ => false,
        });

        app.MapHealthChecks(ReadinessPath);

        return app;
    }
}
