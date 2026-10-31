using Convenciones.Administration;
using Convenciones.Api.Infrastructure;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Convenciones.Api.Endpoints;

/// <summary>
/// Identity administration: the one-time bootstrap, users, the role/capability matrix and the audit trail.
/// </summary>
/// <remarks>
/// <para>
/// Bootstrap is anonymous **and** declared as <c>ServesApiRole()</c>: its database work must run as
/// <c>app_api</c> with no claims, because the one-time window is a row-level policy on
/// <c>platform.user_account</c> (<c>not platform.has_any_user()</c>). Reset to the login's role would
/// bypass it; <c>anon</c> has no INSERT grant.
/// </para>
/// <para>
/// There is no password field anywhere in this file, and a test greps the response schema to prove it
/// (T13 AC 5). Credentials belong to Supabase Auth; the database has no column for them.
/// </para>
/// </remarks>
public static class AdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/auth/bootstrap/status", BootstrapStatusAsync)
            .AllowAnonymous()
            .ServesApiRole()
            .WithTags("Identity")
            .WithName("BootstrapStatus");

        app.MapPost("/auth/bootstrap", BootstrapAsync)
            .AllowAnonymous()
            .ServesApiRole()
            .WithTags("Identity")
            .WithName("Bootstrap");

        var users = app.MapGroup("/users").WithTags("Identity");

        users.MapGet("/", ListUsersAsync)
            .RequiresCapability("user.manage")
            .WithName("ListUsers");

        users.MapGet("/{id:guid}", GetUserAsync)
            .RequiresCapability("user.manage")
            .WithName("GetUser");

        users.MapPost("/", CreateUserAsync)
            .RequiresCapability("user.manage")
            .WithName("CreateUser");

        users.MapPatch("/{id:guid}", UpdateUserAsync)
            .RequiresCapability("user.manage")
            .WithName("UpdateUser");

        users.MapPut("/{id:guid}/roles", ReplaceRolesAsync)
            .RequiresCapability("user.manage")
            .WithName("ReplaceUserRoles");

        users.MapDelete("/{id:guid}", DeleteUserAsync)
            .RequiresCapability("user.manage")
            .WithName("DeleteUser");

        app.MapGet("/roles", ListRolesAsync)
            .RequiresCapability("role.manage")
            .WithTags("Identity")
            .WithName("ListRoles");

        app.MapGet("/capabilities", ListCapabilitiesAsync)
            .RequiresCapability("role.manage")
            .WithTags("Identity")
            .WithName("ListCapabilities");

        app.MapGet("/audit", ListAuditAsync)
            .RequiresCapability("audit.read")
            .WithTags("Audit")
            .WithName("ListAudit");

        return app;
    }

    // ---- Bootstrap -------------------------------------------------------------------------------

    private static async Task<IResult> BootstrapStatusAsync(
        BootstrapStatusHandler handler,
        CancellationToken cancellationToken)
    {
        var status = await handler.HandleAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(status);
    }

    private static async Task<IResult> BootstrapAsync(
        BootstrapRequest request,
        BootstrapHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            return Map(await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false), "/me");
        }
        catch (DbUpdateException exception) when (IsPolicyViolation(exception))
        {
            // The database policy closed the window: a second account cannot be inserted.
            return InvalidState(
                "The system is already configured; the bootstrap window is closed.");
        }
    }

    // ---- Users -----------------------------------------------------------------------------------

    private static async Task<IResult> ListUsersAsync(
        ConvencionesDbContext db,
        ListUsersHandler handler,
        CancellationToken cancellationToken,
        string? q = null,
        int page = 1,
        int pageSize = AdministrationPaging.DefaultPageSize)
    {
        var list = await handler
            .HandleAsync(db.UserAccounts, db.Companies, new ListUsersQuery(q, page, pageSize), cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }

    private static async Task<IResult> GetUserAsync(
        Guid id,
        ConvencionesDbContext db,
        GetUserHandler handler,
        CancellationToken cancellationToken)
    {
        var detail = await handler
            .HandleAsync(db.UserAccounts, db.Companies, id, cancellationToken)
            .ConfigureAwait(false);

        return detail is null ? NotFoundUser(id) : Results.Ok(detail);
    }

    private static Task<IResult> CreateUserAsync(
        CreateUserRequest request,
        CreateUserHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(request, session.UserId, cancellationToken),
                "/users"));

    private static Task<IResult> UpdateUserAsync(
        Guid id,
        UpdateUserRequest request,
        UpdateUserHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(() => handler.HandleAsync(id, request, session.UserId, cancellationToken)));

    private static Task<IResult> ReplaceRolesAsync(
        Guid id,
        ReplaceUserRolesRequest request,
        ReplaceUserRolesHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(
                () => handler.HandleAsync(id, request, session.UserId, cancellationToken)));

    private static Task<IResult> DeleteUserAsync(
        Guid id,
        DeleteUserHandler handler,
        ISessionAccessor accessor,
        CancellationToken cancellationToken) =>
        WithSession(
            accessor,
            session => ExecuteAsync(() => handler.HandleAsync(id, session.UserId, cancellationToken)));

    // ---- Roles, capabilities and audit -----------------------------------------------------------

    private static async Task<IResult> ListRolesAsync(
        ConvencionesDbContext db,
        ListRolesHandler handler,
        CancellationToken cancellationToken)
    {
        var roles = await handler.HandleAsync(db.Roles, cancellationToken).ConfigureAwait(false);
        return Results.Ok(roles);
    }

    private static async Task<IResult> ListCapabilitiesAsync(
        ConvencionesDbContext db,
        ListCapabilitiesHandler handler,
        CancellationToken cancellationToken)
    {
        var capabilities = await handler
            .HandleAsync(db.Capabilities, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(capabilities);
    }

    private static async Task<IResult> ListAuditAsync(
        ConvencionesDbContext db,
        ListAuditHandler handler,
        CancellationToken cancellationToken,
        string? module = null,
        string? entityType = null,
        string? entityId = null,
        Guid? actorId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = AdministrationPaging.DefaultPageSize)
    {
        var list = await handler
            .HandleAsync(
                db.AuditEntries,
                db.UserAccounts,
                new AuditFilter(module, entityType, entityId, actorId, from, to, page, pageSize),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(list);
    }
    // ---- Translation -----------------------------------------------------------------------------

    private static Task<IResult> WithSession(
        ISessionAccessor accessor,
        Func<SessionContext, Task<IResult>> action) =>
        accessor.Session is { } session ? action(session) : Task.FromResult(Forbidden());

    private static async Task<IResult> ExecuteAsync(
        Func<Task<AdministrationCommandResult>> action,
        string? location = null)
    {
        try
        {
            return Map(await action().ConfigureAwait(false), location);
        }
        catch (DbUpdateException exception) when (IsPolicyViolation(exception))
        {
            return InvalidState(
                "The database policy refused the change. Confirm your own role before removing it.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Conflict("That subject is already provisioned.");
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            return Conflict("The row is still referenced elsewhere or points at something missing.");
        }
    }

    private static IResult Map(AdministrationCommandResult result, string? location)
    {
        var uri = new Uri(location ?? "/users", UriKind.Relative);

        return result.Status switch
        {
            AdministrationCommandStatus.Created when result.User is not null =>
                Results.Created(uri, result.User),
            AdministrationCommandStatus.Ok when result.User is not null => Results.Ok(result.User),
            AdministrationCommandStatus.NoContent => Results.NoContent(),
            AdministrationCommandStatus.NotFound => NotFoundProblem(
                result.Detail ?? "The resource does not exist."),
            AdministrationCommandStatus.ValidationFailed => Validation(
                result.Detail ?? "The request is invalid."),
            AdministrationCommandStatus.Conflict => Conflict(
                result.Detail ?? "The request conflicts with the current state."),
            AdministrationCommandStatus.InvalidState => InvalidState(
                result.Detail ?? "The operation is not allowed."),
            _ => Conflict(result.Detail ?? "The request conflicts with the current state."),
        };
    }

    private static bool IsPolicyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "42501" };

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23505" };

    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "23503" };

    private static IResult Forbidden(string? detail = null) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: detail ?? "No session was resolved for this request.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Forbidden });

    private static IResult Validation(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Validation failed",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.ValidationFailed });

    private static IResult Conflict(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.Conflict });

    private static IResult InvalidState(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Invalid state",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.InvalidState });

    private static IResult NotFoundProblem(string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });

    private static IResult NotFoundUser(Guid id) =>
        NotFoundProblem($"No user with id '{id}' exists.");
}
