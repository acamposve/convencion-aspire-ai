using System.Text.Json;
using Convenciones.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Convenciones.Administration;

/// <summary>Paging bounds for the administration lists.</summary>
public static class AdministrationPaging
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    internal static (int Page, int PageSize) Normalise(int page, int pageSize) =>
        (page < 1 ? 1 : page,
         pageSize is < 1 or > MaxPageSize ? DefaultPageSize : pageSize);
}

/// <summary>Maps the aggregate and the platform rows to the response records.</summary>
public static class AdministrationResponses
{
    public static UserSummary Summary(UserAccount user, string? companyName) =>
        new(
            user.Id,
            user.Email,
            user.DisplayName,
            user.IsBlocked,
            user.ExpiresOn,
            user.Profile?.CompanyId,
            companyName,
            user.RoleKeys,
            user.CreatedAt,
            user.UpdatedAt);

    public static UserDetail Detail(UserAccount user, string? companyName) =>
        new(
            user.Id,
            user.Email,
            user.DisplayName,
            user.Profile?.Phone,
            user.Profile?.CompanyId,
            companyName,
            user.IsBlocked,
            user.ExpiresOn,
            user.RoleKeys,
            user.CreatedAt,
            user.UpdatedAt);

    public static RoleResponse Role(Role role) =>
        new(
            role.Id,
            role.Key,
            role.Name,
            role.Description,
            role.IsSystem,
            role.Capabilities
                .Select(grant => grant.Capability?.Key)
                .Where(key => key is not null)
                .Select(key => key!)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray());
}

// ---- Users ---------------------------------------------------------------------------------------

public sealed record ListUsersQuery(string? Term, int Page, int PageSize);

public sealed class ListUsersHandler
{
    public async Task<UserListResponse> HandleAsync(
        IQueryable<UserAccount> users,
        IQueryable<Company> companies,
        ListUsersQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(companies);
        ArgumentNullException.ThrowIfNull(query);

        var (page, pageSize) = AdministrationPaging.Normalise(query.Page, query.PageSize);
        var filtered = users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Term))
        {
            var term = query.Term.Trim();
            filtered = filtered.Where(user =>
                EF.Functions.ILike(user.Email, $"%{term}%")
                || EF.Functions.ILike(user.DisplayName, $"%{term}%"));
        }

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await filtered
            .Include(user => user.Profile)
            .Include(user => user.Roles)
                .ThenInclude(assignment => assignment.Role)
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var companyNames = await CompanyNamesAsync(
            companies, rows, cancellationToken).ConfigureAwait(false);

        return new UserListResponse(
            rows.Select(user => AdministrationResponses.Summary(
                user, FindCompanyName(companyNames, user))).ToArray(),
            page,
            pageSize,
            total);
    }

    internal static async Task<Dictionary<Guid, string>> CompanyNamesAsync(
        IQueryable<Company> companies,
        IEnumerable<UserAccount> users,
        CancellationToken cancellationToken)
    {
        var ids = users
            .Select(user => user.Profile?.CompanyId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        if (ids.Length == 0)
        {
            return [];
        }

        return await companies
            .AsNoTracking()
            .Where(company => ids.Contains(company.Id))
            .ToDictionaryAsync(company => company.Id, company => company.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string? FindCompanyName(
        IReadOnlyDictionary<Guid, string> names,
        UserAccount user) =>
        user.Profile?.CompanyId is { } companyId && names.TryGetValue(companyId, out var name)
            ? name
            : null;
}

public sealed class GetUserHandler
{
    public async Task<UserDetail?> HandleAsync(
        IQueryable<UserAccount> users,
        IQueryable<Company> companies,
        Guid userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(companies);

        var user = await users
            .AsNoTracking()
            .Include(entity => entity.Profile)
            .Include(entity => entity.Roles)
                .ThenInclude(assignment => assignment.Role)
            .SingleOrDefaultAsync(entity => entity.Id == userId, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            return null;
        }

        var names = await ListUsersHandler
            .CompanyNamesAsync(companies, [user], cancellationToken)
            .ConfigureAwait(false);

        return AdministrationResponses.Detail(
            user, ListUsersHandler.FindCompanyName(names, user));
    }
}

/// <summary>Whether the one-time window is still open. Read from <c>platform.has_any_user()</c>.</summary>
public sealed class BootstrapStatusHandler(IAdministrationStore store)
{
    public async Task<BootstrapStatusResponse> HandleAsync(CancellationToken cancellationToken)
    {
        var anyUser = await store.AnyUserAsync(cancellationToken).ConfigureAwait(false);
        return new BootstrapStatusResponse(anyUser);
    }
}

// ---- Roles and capabilities ----------------------------------------------------------------------

public sealed class ListRolesHandler
{
    public async Task<IReadOnlyList<RoleResponse>> HandleAsync(
        IQueryable<Role> roles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var rows = await roles
            .AsNoTracking()
            .Include(role => role.Capabilities)
                .ThenInclude(grant => grant.Capability)
            .OrderBy(role => role.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(AdministrationResponses.Role).ToArray();
    }
}

public sealed class ListCapabilitiesHandler
{
    public async Task<IReadOnlyList<CapabilityResponse>> HandleAsync(
        IQueryable<Capability> capabilities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var rows = await capabilities
            .AsNoTracking()
            .OrderBy(capability => capability.Key)
            .Select(capability => new CapabilityResponse(capability.Key, capability.Description))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows;
    }
}

// ---- Audit ---------------------------------------------------------------------------------------

public sealed record AuditFilter(
    string? Module,
    string? EntityType,
    string? EntityId,
    Guid? ActorId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize);

/// <summary>
/// The audit row as the API projects it. The handler never sees the <c>AuditEntry</c> entity, so this
/// module stays free of the API's persistence types.
/// </summary>
public sealed record AuditProjection(
    long Id,
    DateTimeOffset OccurredAt,
    Guid? ActorId,
    string? ActorDisplayName,
    string Module,
    string EntityType,
    string EntityId,
    string Action,
    string? FromState,
    string? ToState,
    string? ChangesJson);

public sealed class ListAuditHandler
{
    public async Task<AuditListResponse> HandleAsync(
        IQueryable<AuditEntry> entries,
        IQueryable<UserAccount> users,
        AuditFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(filter);

        var (page, pageSize) = AdministrationPaging.Normalise(filter.Page, filter.PageSize);
        var filtered = entries.AsNoTracking();

        // Filters are applied to the columns, before the response is shaped: filtering over a projected
        // record is not something the query translator can push down.
        if (!string.IsNullOrWhiteSpace(filter.Module))
        {
            var module = filter.Module.Trim();
            filtered = filtered.Where(entry => entry.Module == module);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            var entityType = filter.EntityType.Trim();
            filtered = filtered.Where(entry => entry.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
        {
            var entityId = filter.EntityId.Trim();
            filtered = filtered.Where(entry => entry.EntityId == entityId);
        }

        if (filter.ActorId is { } actorId)
        {
            filtered = filtered.Where(entry => entry.ActorId == actorId);
        }

        if (filter.From is { } from)
        {
            filtered = filtered.Where(entry => entry.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            filtered = filtered.Where(entry => entry.OccurredAt <= to);
        }

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await filtered
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var actorIds = rows
            .Where(entry => entry.ActorId is not null)
            .Select(entry => entry.ActorId!.Value)
            .Distinct()
            .ToArray();

        var actorNames = actorIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await users
                .AsNoTracking()
                .Where(user => actorIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.DisplayName, cancellationToken)
                .ConfigureAwait(false);

        return new AuditListResponse(
            rows.Select(entry => ToResponse(entry, actorNames)).ToArray(),
            page,
            pageSize,
            total);
    }

    private static AuditEntryResponse ToResponse(
        AuditEntry entry,
        IReadOnlyDictionary<Guid, string> actorNames) =>
        new(
            entry.Id,
            entry.OccurredAt,
            entry.ActorId,
            entry.ActorId is { } actorId && actorNames.TryGetValue(actorId, out var name) ? name : null,
            entry.Module,
            entry.EntityType,
            entry.EntityId,
            entry.Action,
            entry.FromState,
            entry.ToState,
            ParseChanges(entry.Changes));

    private static JsonElement? ParseChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}