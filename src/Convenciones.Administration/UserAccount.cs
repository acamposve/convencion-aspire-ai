namespace Convenciones.Administration;

/// <summary>
/// A provisioned user: the account row, the optional profile and the granted roles.
/// </summary>
/// <remarks>
/// <para>
/// There is no password anywhere, by design: Supabase Auth owns credentials, the database has no password
/// column, and no endpoint accepts or returns one (T13 AC 5). The account's <see cref="Id"/> is the
/// subject of the tokens Supabase issues, which is why creating a user means telling the API that subject
/// rather than inviting an address.
/// </para>
/// <para>
/// The domain enforces the rules the database cannot express without knowing the caller: an account
/// cannot block itself and the last administrative role cannot be removed from the caller's own account
/// (the 409 that prevents an unrecoverable lockout, T13 AC 3).
/// </para>
/// </remarks>
public sealed class UserAccount
{
    private UserAccount()
    {
    }

    public Guid Id { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public bool IsBlocked { get; private set; }

    public DateOnly? ExpiresOn { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public UserProfile? Profile { get; private set; }

    public List<UserRole> Roles { get; private set; } = [];

    public static UserAccount Create(
        Guid subject,
        string? email,
        string? displayName,
        Guid? actorId,
        DateTimeOffset now)
    {
        if (subject == Guid.Empty)
        {
            throw AdministrationRuleException.Validation(
                "The subject is required: it is the identity Supabase Auth issues tokens for.");
        }

        return new UserAccount
        {
            Id = subject,
            Email = AdministrationText.Email(email),
            DisplayName = AdministrationText.Required(displayName, "The display name is required."),
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
            Profile = UserProfile.Create(subject, now),
        };
    }

    public void Rename(string? displayName, Guid? actorId, DateTimeOffset now)
    {
        DisplayName = AdministrationText.Required(displayName, "The display name is required.");
        Touch(actorId, now);
    }

    public void ChangeEmail(string? email, Guid? actorId, DateTimeOffset now)
    {
        Email = AdministrationText.Email(email);
        Touch(actorId, now);
    }

    public void SetBlocked(bool blocked, Guid? actorId, DateTimeOffset now)
    {
        IsBlocked = blocked;
        Touch(actorId, now);
    }

    public void SetExpiry(DateOnly? expiresOn, Guid? actorId, DateTimeOffset now)
    {
        ExpiresOn = expiresOn;
        Touch(actorId, now);
    }

    public void SetProfile(string? phone, Guid? companyId, Guid? actorId, DateTimeOffset now)
    {
        Profile ??= UserProfile.Create(Id, now);
        Profile.Apply(phone, companyId, now);
        Touch(actorId, now);
    }

    public void ReplaceRoles(IEnumerable<Guid> roleIds, Guid? actorId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        var wanted = roleIds.Distinct().ToArray();

        Roles.RemoveAll(assignment => !wanted.Contains(assignment.RoleId));

        foreach (var roleId in wanted.Where(roleId => Roles.All(a => a.RoleId != roleId)))
        {
            Roles.Add(UserRole.Create(Id, roleId, actorId, now));
        }

        Touch(actorId, now);
    }

    public bool HasCapability(string capability) =>
        Roles.Any(assignment =>
            assignment.Role?.Capabilities.Any(grant => grant.Capability?.Key == capability) == true);

    public IReadOnlyList<string> RoleKeys =>
        Roles
            .Where(assignment => assignment.Role is not null)
            .Select(assignment => assignment.Role!.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

    private void Touch(Guid? actorId, DateTimeOffset now)
    {
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}

public sealed class UserProfile
{
    private UserProfile()
    {
    }

    public Guid UserId { get; private set; }

    public string? Phone { get; private set; }

    public Guid? CompanyId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    internal static UserProfile Create(Guid userId, DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

    internal void Apply(string? phone, Guid? companyId, DateTimeOffset now)
    {
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        CompanyId = companyId;
        UpdatedAt = now;
    }
}

/// <summary>One granted role. The relationship table carries who granted it and when.</summary>
public sealed class UserRole
{
    private UserRole()
    {
    }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public Guid? GrantedBy { get; private set; }

    public Role? Role { get; private set; }

    internal static UserRole Create(Guid userId, Guid roleId, Guid? grantedBy, DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            RoleId = roleId,
            GrantedAt = now,
            GrantedBy = grantedBy,
        };
}

/// <summary>Read-side role, with the capability keys it grants (the matrix the admin screen shows).</summary>
public sealed class Role
{
    private Role()
    {
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsSystem { get; private set; }

    public List<RoleCapability> Capabilities { get; private set; } = [];
}

public sealed class RoleCapability
{
    private RoleCapability()
    {
    }

    public Guid RoleId { get; private set; }

    public Guid CapabilityId { get; private set; }

    public Capability? Capability { get; private set; }
}

public sealed class Capability
{
    private Capability()
    {
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;
}

internal static class AdministrationText
{
    private static readonly System.Text.RegularExpressions.Regex EmailPattern = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static string Required(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw AdministrationRuleException.Validation(message);
        }

        return value.Trim();
    }

    public static string Email(string? value)
    {
        var email = Required(value, "The email is required.");

        if (!EmailPattern.IsMatch(email))
        {
            throw AdministrationRuleException.Validation("The email is not a valid address.");
        }

        return email;
    }

    public static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
