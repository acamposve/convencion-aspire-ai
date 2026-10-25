using Convenciones.Administration;
using Convenciones.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

using Convenciones.Catalog;

namespace Convenciones.Api.Infrastructure;

/// <summary>The API's implementation of the administration persistence port.</summary>
/// <remarks>
/// <c>AnyUserAsync</c> calls <c>platform.has_any_user()</c> rather than querying the table: the read policy
/// on <c>user_account</c> only lets a caller see their own row (or every row with <c>user.manage</c>), so a
/// plain <c>AnyAsync</c> would report "no users" during bootstrap and reopen the window.
/// </remarks>
public sealed class AdministrationStore(ConvencionesDbContext db) : IAdministrationStore
{
    public Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        db.UserAccounts
            .Include(user => user.Profile)
            .Include(user => user.Roles)
                .ThenInclude(assignment => assignment.Role)
                    .ThenInclude(role => role!.Capabilities)
                        .ThenInclude(grant => grant.Capability)
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);

    public async Task<bool> AnyUserAsync(CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<bool>($"select platform.has_any_user() as \"Value\"")
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Role>> RolesAsync(CancellationToken cancellationToken) =>
        await db.Roles
            .Include(role => role.Capabilities)
                .ThenInclude(grant => grant.Capability)
            .OrderBy(role => role.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<string?> CompanyNameAsync(Guid? companyId, CancellationToken cancellationToken) =>
        companyId is null
            ? Task.FromResult<string?>(null)
            : db.Companies
                .AsNoTracking()
                .Where(company => company.Id == companyId)
                .Select(company => company.Name)
                .SingleOrDefaultAsync(cancellationToken)!;

    public async Task<BootstrapResult> BootstrapAsync(
        Guid subject,
        string email,
        string displayName,
        string roleKey,
        CancellationToken cancellationToken)
    {
        // SECURITY DEFINER in the database: it locks the account table, refuses when an account exists,
        // and writes the account, profile, role grant and audit row in one statement.
        var row = await db.Database
            .SqlQuery<BootstrapRow>(
                $"""
                 select id as "Id", created_at as "CreatedAt"
                 from platform.bootstrap_first_admin({subject}, {email}, {displayName}, {roleKey})
                 """)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BootstrapResult(row.Id, row.CreatedAt);
    }

    public void Add(UserAccount user) => db.UserAccounts.Add(user);

    public void Remove(UserAccount user) => db.UserAccounts.Remove(user);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>Adds an audit row to the same unit of work as the change it records.</summary>
public sealed class AuditTrail(ConvencionesDbContext db) : IAuditTrail
{
    public void Record(
        string entityType,
        string entityId,
        string action,
        Guid? actorId,
        object? changes,
        string? fromState = null,
        string? toState = null)
    {
        var json = changes is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(changes);

        db.AuditEntries.Add(AuditEntry.Record(
            actorId,
            "identity",
            entityType,
            entityId,
            action,
            json,
            System.Diagnostics.Activity.Current?.TraceId.ToString(),
            fromState,
            toState));
    }
}

/// <summary>The shape <c>platform.bootstrap_first_admin</c> returns, for the SQL query materialiser.</summary>
internal sealed class BootstrapRow
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
