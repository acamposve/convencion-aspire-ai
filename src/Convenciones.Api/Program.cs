using Convenciones.Api.Infrastructure;
using Convenciones.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ---- Error contract -------------------------------------------------------------------------
// Every unhandled failure becomes an RFC 9457 problem response with a correlation id.
// See docs/architecture/api-contracts.md §2.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ---- Data access ----------------------------------------------------------------------------
// Fail fast: a missing connection string must stop startup, never produce an API that answers
// requests it cannot serve. There is deliberately no default value.
var connectionString = builder.Configuration.GetConnectionString("convenciones");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'convenciones' is not configured. Under Aspire it is injected as " +
        "ConnectionStrings__convenciones; outside Aspire set it in configuration or environment.");
}

builder.Services.AddDbContext<ConvencionesDbContext>(options => options.UseNpgsql(connectionString));

builder.Services
    .AddHealthChecks()
    .AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();

app.MapDefaultEndpoints();

app.Run();

/// <summary>Entry point marker so integration tests can host the real pipeline.</summary>
public partial class Program;
