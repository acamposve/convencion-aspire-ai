using Convenciones.Agreements;
using Convenciones.Api.Endpoints;
using Convenciones.Api.Infrastructure;
using Convenciones.Documents;
using Convenciones.Identity;
using Convenciones.Legal;
using Convenciones.Negotiation;
using Convenciones.ServiceDefaults;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ---- Error contract -------------------------------------------------------------------------
// Every unhandled failure becomes an RFC 9457 problem response with a correlation id.
// See docs/architecture/api-contracts.md §2.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// A request the model binder cannot read must become a problem response, not a bare status. Left at
// the framework default, a binding failure is answered with 400 and an empty body in every
// environment except Development, which is exactly the silent failure the contract forbids. Throwing
// hands the failure to GlobalExceptionHandler, which reports malformed_request.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

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

// The audit interceptor reads the change tracker, so a change cannot be saved without its audit row.
// The session interceptor puts the caller's claims on the business connection, so RLS policies and
// capability guard triggers evaluate the real caller rather than an anonymous pooled connection.
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<DatabaseSessionInterceptor>();

// EF Core 9+ maps PostgreSQL enums through the provider options. Doing it on a NpgsqlDataSource
// instead leaves EF's built-in enum-to-integer conversion in place, and the database then rejects the
// insert with "column extraction_status is of type platform.extraction_status but expression is of
// type integer". The schema must be named because the type lives in platform, not public.
builder.Services.AddDbContext<ConvencionesDbContext>((serviceProvider, options) => options
    .UseNpgsql(connectionString, npgsql => npgsql
        .MapEnum<ExtractionStatus>("extraction_status", "platform", ExactNameTranslator.Instance)
        .MapEnum<PublicationState>("publication_state", "app", ExactNameTranslator.Instance)
        .MapEnum<StatuteKind>("statute_kind", "app", ExactNameTranslator.Instance)
        .MapEnum<CaseState>("negotiation_case_state", "app", ExactNameTranslator.Instance)
        .MapEnum<PetitionState>("petition_state", "app", ExactNameTranslator.Instance)
        .MapEnum<PartyRole>("party_role", "app", ExactNameTranslator.Instance))
    .AddInterceptors(
        serviceProvider.GetRequiredService<AuditInterceptor>(),
        serviceProvider.GetRequiredService<DatabaseSessionInterceptor>()));

// ---- Request limits -----------------------------------------------------------------------------
// Documents are capped at 20 MiB by a database constraint; the transport limit sits just above it so
// an oversized upload is refused while reading, not after the whole body has been buffered. The
// framework default (30 MB) would let a request through that the database must then reject.
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = (22L * 1024 * 1024));

// The three limits must agree, or the rejection happens at the wrong layer: 20 MiB is the business
// rule, the multipart limit adds headroom for part headers and boundaries, and the transport limit
// must exceed the multipart one so the parse fails first with a message we control. A small memory
// threshold keeps a 20 MiB upload out of the managed heap.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 22L * 1024 * 1024;
    options.MultipartHeadersLengthLimit = 16 * 1024;
    options.MemoryBufferThreshold = 64 * 1024;
});

// ---- Document storage ----------------------------------------------------------------------------
// The local adapter is a development substitute for Supabase Storage, which is T18 and needs an
// approved project. Failing fast on a missing root keeps a misconfigured deployment from writing
// documents somewhere unintended.
var storageRoot = builder.Configuration["Documents:StorageRoot"];
if (string.IsNullOrWhiteSpace(storageRoot))
{
    throw new InvalidOperationException(
        "Documents:StorageRoot is not configured. Documents are kept in a private directory until "
        + "Supabase Storage is approved in T18.");
}

builder.Services.AddSingleton<IDocumentStorage>(_ => new LocalDocumentStorage(storageRoot));

// ---- Legal library ------------------------------------------------------------------------------
// Statutes are company-independent reference data. The module owns its commands and queries; it
// reaches persistence only through IStatuteStore, implemented here, so it never sees the DbContext.
builder.Services.AddScoped<IStatuteStore, StatuteStore>();
builder.Services.AddScoped<CreateStatuteHandler>();
builder.Services.AddScoped<UpdateStatuteHandler>();
builder.Services.AddScoped<AddStatuteArticleHandler>();
builder.Services.AddScoped<UpdateStatuteArticleHandler>();
builder.Services.AddScoped<DeleteStatuteArticleHandler>();
builder.Services.AddScoped<ListStatutesHandler>();
builder.Services.AddScoped<GetStatuteHandler>();
builder.Services.AddScoped<ListStatuteArticlesHandler>();
builder.Services.AddScoped<GetStatuteArticleHandler>();
builder.Services.AddScoped<GetStatuteArticlesForHeadingHandler>();

// ---- Negotiation ---------------------------------------------------------------------------------
// Cases are company-scoped; promotion creates the agreement through IPromotedAgreementFactory so both
// writes share one DbContext and one transaction (T11 AC 3).
builder.Services.AddScoped<INegotiationStore, NegotiationStore>();
builder.Services.AddScoped<IPromotedAgreementFactory, PromotedAgreementFactory>();
builder.Services.AddScoped<CreateCaseHandler>();
builder.Services.AddScoped<AddPartyHandler>();
builder.Services.AddScoped<RemovePartyHandler>();
builder.Services.AddScoped<AddPetitionHandler>();
builder.Services.AddScoped<UpdatePetitionHandler>();
builder.Services.AddScoped<DeletePetitionHandler>();
builder.Services.AddScoped<ChangePetitionStateHandler>();
builder.Services.AddScoped<AddOfferHandler>();
builder.Services.AddScoped<DeleteOfferHandler>();
builder.Services.AddScoped<AddMeetingHandler>();
builder.Services.AddScoped<DeleteMeetingHandler>();
builder.Services.AddScoped<AddMeetingAgreementHandler>();
builder.Services.AddScoped<DeleteMeetingAgreementHandler>();
builder.Services.AddScoped<TransitionCaseHandler>();
builder.Services.AddScoped<PromoteCaseHandler>();
builder.Services.AddScoped<ListCasesHandler>();
builder.Services.AddScoped<GetCaseHandler>();

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

// Two refusals never reach an endpoint and are written by the framework itself: an unsupported
// content type (415) and a body over the transport limit (413). They would otherwise be a bare status
// with an empty body, which the contract forbids; this adds the body for exactly those two and leaves
// every other response, status included, untouched.
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;

    if (RequestFailureProblem.For(statusCodeContext.HttpContext, response.StatusCode) is not { } problem)
    {
        return;
    }

    await statusCodeContext.HttpContext.RequestServices
        .GetRequiredService<IProblemDetailsService>()
        .TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = statusCodeContext.HttpContext,
            ProblemDetails = problem,
        })
        .ConfigureAwait(false);
});

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Resolves the caller's session from the database and enforces the endpoint's declared capability.
app.UseMiddleware<SessionResolutionMiddleware>();

app.MapDefaultEndpoints();
app.MapMeEndpoints();
app.MapCompanyEndpoints();
app.MapDocumentEndpoints();
app.MapDocumentExtractionEndpoints();
app.MapAgreementEndpoints();
app.MapComparisonHeadingEndpoints();
app.MapComparisonEndpoints();
app.MapStatuteEndpoints();
app.MapNegotiationEndpoints();

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
