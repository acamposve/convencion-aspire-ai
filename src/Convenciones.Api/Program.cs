using Convenciones.Api.Endpoints;
using Convenciones.Api.Infrastructure;
using Convenciones.Identity;
using Convenciones.ServiceDefaults;
using Microsoft.AspNetCore.Routing;
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

// ---- Request limits -----------------------------------------------------------------------------
// Documents are capped at 20 MiB by a database constraint; the transport limit sits just above it so
// an oversized upload is refused while reading, not after the whole body has been buffered. The
// framework default (30 MB) would let a request through that the database must then reject.
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = (21L * 1024 * 1024));

// ---- CORS ---------------------------------------------------------------------------------------
// No wildcard and no default origin: the browser client is served from a known host, and a policy of
// "any origin" would turn every token a user holds into something any website could use.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length == 0)
    {
        // No origins configured means no browser client is authorised. Absence of configuration must
        // not silently become permission for everyone.
        policy.WithOrigins("https://localhost");
        return;
    }

    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

// ---- Authentication and authorization ---------------------------------------------------------
// Fail fast again: without a way to validate signatures there is no authentication, only a claim
// that there is. See docs/architecture/security-model.md.
builder.Services.AddConvencionesIdentity(builder.Configuration, connectionString);

builder.Services
    .AddHealthChecks()
    .AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Resolves the caller's session from the database and enforces the endpoint's declared capability.
app.UseMiddleware<SessionResolutionMiddleware>();

app.MapDefaultEndpoints();
app.MapMeEndpoints();
app.MapCompanyEndpoints();

// ---- Startup gate -----------------------------------------------------------------------------
// An endpoint that does not declare its security requirement stops the host from starting. This is
// deliberate: a missed declaration is a data leak, and finding it here means the person who wrote it
// finds it, not whoever notices the hole later.
//
// The endpoints come from the application's own data sources. Resolving EndpointDataSource from DI
// returns nothing at this point, and a validator that inspects nothing reports success for a
// pipeline it never looked at — so the validator itself refuses an empty set.
var routes = ((IEndpointRouteBuilder)app).DataSources
    .SelectMany(source => source.Endpoints)
    .OfType<RouteEndpoint>()
    .ToArray();

EndpointSecurityValidator.Validate(routes);

// Exposed so the tests can assert the enumeration is real rather than trust it.
Program.ValidatedRoutes = routes;

app.Run();

/// <summary>Entry point marker so integration tests can host the real pipeline.</summary>
public partial class Program
{
    /// <summary>
    /// The routed endpoints that passed the startup security gate on this host.
    /// </summary>
    /// <remarks>
    /// Populated at startup so a test can verify that every route declares its requirement against
    /// the real route table, instead of against an empty list that would pass for the wrong reason.
    /// </remarks>
    internal static IReadOnlyList<RouteEndpoint> ValidatedRoutes { get; set; } = [];
}
