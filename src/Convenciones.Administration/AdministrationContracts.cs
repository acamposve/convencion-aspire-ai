using System.Text.Json;
using Convenciones.Catalog;

namespace Convenciones.Administration;

public enum AdministrationRuleKind
{
    Validation,
    Conflict,
}

public sealed class AdministrationRuleException(AdministrationRuleKind kind, string message)
    : Exception(message)
{
    public AdministrationRuleKind Kind { get; } = kind;

    public static AdministrationRuleException Validation(string message) =>
        new(AdministrationRuleKind.Validation, message);

    public static AdministrationRuleException Conflict(string message) =>
        new(AdministrationRuleKind.Conflict, message);
}

// ---- Audit ---------------------------------------------------------------------------------------

/// <summary>
/// Writes one audit row in the same transaction as the change it describes.
/// </summary>
/// <remarks>
/// The interceptor audits entities that implement <see cref="IAuditable"/>. User changes span three
/// tables (account, profile, roles) and are one administrative act, so they are audited explicitly
/// rather than once per table. Personal data stays out of the payload: field names and non-personal
/// values only (<c>security-model.md</c> §5.5).
/// </remarks>
public interface IAuditTrail
{
    void Record(
        string entityType,
        string entityId,
        string action,
        Guid? actorId,
        object? changes,
        string? fromState = null,
        string? toState = null);
}

// ---- Persistence ---------------------------------------------------------------------------------

public interface IAdministrationStore
{
    Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The one-time bootstrap window: true while no account exists at all.</summary>
    Task<bool> AnyUserAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Role>> RolesAsync(CancellationToken cancellationToken);

    Task<string?> CompanyNameAsync(Guid? companyId, CancellationToken cancellationToken);

    void Add(UserAccount user);

    void Remove(UserAccount user);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>platform.bootstrap_first_admin</c>: it locks the account table, refuses when any account
    /// exists, and writes the account, profile, role grant and audit row atomically.
    /// </summary>
    Task<BootstrapResult> BootstrapAsync(
        Guid subject,
        string email,
        string displayName,
        string roleKey,
        CancellationToken cancellationToken);
}

/// <summary>What the bootstrap function returns: the account it created and when.</summary>
public sealed record BootstrapResult(Guid Id, DateTimeOffset CreatedAt);

// ---- Contracts -----------------------------------------------------------------------------------

public sealed record BootstrapRequest(Guid Subject, string? Email, string? DisplayName);

public sealed record BootstrapStatusResponse(bool Configured);

public sealed record CreateUserRequest(
    Guid Subject,
    string? Email,
    string? DisplayName,
    string? Phone,
    Guid? CompanyId,
    IReadOnlyList<string>? RoleKeys,
    bool? IsBlocked,
    DateOnly? ExpiresOn);

/// <summary>Partial update: an absent field keeps its value, an explicit null clears a nullable one.</summary>
public sealed record UpdateUserRequest(
    PatchField<string?> DisplayName,
    PatchField<string?> Email,
    PatchField<string?> Phone,
    PatchField<Guid?> CompanyId,
    PatchField<bool?> IsBlocked,
    PatchField<DateOnly?> ExpiresOn);

public sealed record ReplaceUserRolesRequest(IReadOnlyList<string>? RoleKeys);

public sealed record UserSummary(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsBlocked,
    DateOnly? ExpiresOn,
    Guid? CompanyId,
    string? CompanyName,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserDetail(
    Guid Id,
    string Email,
    string DisplayName,
    string? Phone,
    Guid? CompanyId,
    string? CompanyName,
    bool IsBlocked,
    DateOnly? ExpiresOn,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserListResponse(
    IReadOnlyList<UserSummary> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record RoleResponse(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    bool IsSystem,
    IReadOnlyList<string> Capabilities);

public sealed record CapabilityResponse(string Key, string Description);

public sealed record AuditEntryResponse(
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
    JsonElement? Changes);

public sealed record AuditListResponse(
    IReadOnlyList<AuditEntryResponse> Items,
    int Page,
    int PageSize,
    int Total);

// ---- Results -------------------------------------------------------------------------------------

public enum AdministrationCommandStatus
{
    Ok,
    Created,
    NoContent,
    NotFound,
    ValidationFailed,
    Conflict,
    InvalidState,
}

public sealed record AdministrationCommandResult(
    AdministrationCommandStatus Status,
    string? Detail = null,
    UserDetail? User = null)
{
    public static AdministrationCommandResult Created(UserDetail user) =>
        new(AdministrationCommandStatus.Created, User: user);

    public static AdministrationCommandResult Ok(UserDetail user) =>
        new(AdministrationCommandStatus.Ok, User: user);

    public static AdministrationCommandResult Deleted() => new(AdministrationCommandStatus.NoContent);

    public static AdministrationCommandResult NotFound(string detail) =>
        new(AdministrationCommandStatus.NotFound, detail);

    public static AdministrationCommandResult Validation(string detail) =>
        new(AdministrationCommandStatus.ValidationFailed, detail);

    public static AdministrationCommandResult Conflict(string detail) =>
        new(AdministrationCommandStatus.Conflict, detail);

    public static AdministrationCommandResult InvalidState(string detail) =>
        new(AdministrationCommandStatus.InvalidState, detail);

    internal static AdministrationCommandResult FromRule(AdministrationRuleException rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Kind is AdministrationRuleKind.Validation
            ? Validation(rule.Message)
            : Conflict(rule.Message);
    }
}
