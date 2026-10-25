namespace Convenciones.Administration;

/// <summary>
/// The bootstrap that creates the first SuperAdmin (T13 AC 1, Art. VI.6).
/// </summary>
/// <remarks>
/// The provisioning itself is <c>platform.bootstrap_first_admin</c>: one atomic database function that
/// locks the account table, refuses when an account already exists, and writes the account, profile, role
/// grant and audit row together. This handler validates the payload, checks the window first so the normal
/// case gets a clean 409, and translates the function's refusal when two calls race.
/// </remarks>
public sealed class BootstrapHandler(IAdministrationStore store)
{
    public const string SuperAdminRoleKey = "superadmin";

    public async Task<AdministrationCommandResult> HandleAsync(
        BootstrapRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await store.AnyUserAsync(cancellationToken).ConfigureAwait(false))
        {
            return AdministrationCommandResult.InvalidState(
                "The system is already configured; the bootstrap window is closed.");
        }

        string email;
        string displayName;

        try
        {
            email = AdministrationText.Email(request.Email);
            displayName = AdministrationText.Required(request.DisplayName, "The display name is required.");
        }
        catch (AdministrationRuleException rule)
        {
            return AdministrationCommandResult.FromRule(rule);
        }

        BootstrapResult created;

        try
        {
            created = await store
                .BootstrapAsync(request.Subject, email, displayName, SuperAdminRoleKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsClosedWindow(exception))
        {
            return AdministrationCommandResult.InvalidState(
                "The system is already configured; the bootstrap window is closed.");
        }

        return AdministrationCommandResult.Created(new UserDetail(
            created.Id,
            email,
            displayName,
            Phone: null,
            CompanyId: null,
            CompanyName: null,
            IsBlocked: false,
            ExpiresOn: null,
            Roles: [SuperAdminRoleKey],
            CreatedAt: created.CreatedAt,
            UpdatedAt: created.CreatedAt));
    }

    private static bool IsClosedWindow(Exception exception) =>
        exception is Npgsql.PostgresException { SqlState: "22023" }
        || exception.InnerException is Npgsql.PostgresException { SqlState: "22023" };
}
public sealed class CreateUserHandler(IAdministrationStore store, IAuditTrail audit)
{
    public async Task<AdministrationCommandResult> HandleAsync(
        CreateUserRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var roles = await store.RolesAsync(cancellationToken).ConfigureAwait(false);

        if (!TryResolveRoles(roles, request.RoleKeys, out var roleIds, out var unknown))
        {
            return AdministrationCommandResult.Validation($"Unknown role: '{unknown}'.");
        }

        var now = DateTimeOffset.UtcNow;
        UserAccount user;

        try
        {
            user = UserAccount.Create(request.Subject, request.Email, request.DisplayName, actorId, now);

            if (roleIds.Count > 0)
            {
                user.ReplaceRoles(roleIds, actorId, now);
            }

            if (request.IsBlocked is { } blocked)
            {
                user.SetBlocked(blocked, actorId, now);
            }

            if (request.ExpiresOn is { } expiry)
            {
                user.SetExpiry(expiry, actorId, now);
            }

            if (request.Phone is not null || request.CompanyId is not null)
            {
                user.SetProfile(request.Phone, request.CompanyId, actorId, now);
            }
        }
        catch (AdministrationRuleException rule)
        {
            return AdministrationCommandResult.FromRule(rule);
        }

        store.Add(user);

        audit.Record(
            "user_account",
            user.Id.ToString(),
            "created",
            actorId,
            new
            {
                roles = user.RoleKeys,
                isBlocked = user.IsBlocked,
                expiresOn = user.ExpiresOn,
                companyId = user.Profile?.CompanyId,
            });

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return AdministrationCommandResult.Created(
            AdministrationResponses.Detail(user, await CompanyNameAsync(store, user, cancellationToken).ConfigureAwait(false)));
    }

    internal static bool TryResolveRoles(
        IReadOnlyList<Role> roles,
        IReadOnlyList<string>? keys,
        out List<Guid> roleIds,
        out string? unknown)
    {
        roleIds = [];
        unknown = null;

        foreach (var key in keys ?? [])
        {
            var role = roles.SingleOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));

            if (role is null)
            {
                unknown = key;
                return false;
            }

            roleIds.Add(role.Id);
        }

        return true;
    }

    internal static async Task<string?> CompanyNameAsync(
        IAdministrationStore store,
        UserAccount user,
        CancellationToken cancellationToken) =>
        await store.CompanyNameAsync(user.Profile?.CompanyId, cancellationToken).ConfigureAwait(false);
}

public sealed class UpdateUserHandler(IAdministrationStore store, IAuditTrail audit)
{
    public async Task<AdministrationCommandResult> HandleAsync(
        Guid userId,
        UpdateUserRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await store.FindAsync(userId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return AdministrationCommandResult.NotFound($"No user with id '{userId}' exists.");
        }

        if (request.DisplayName.IsSet && request.DisplayName.Value is null)
        {
            return AdministrationCommandResult.Validation("The display name is required.");
        }

        if (request.Email.IsSet && request.Email.Value is null)
        {
            return AdministrationCommandResult.Validation("The email is required.");
        }

        if (request.IsBlocked.IsSet && request.IsBlocked.Value is true && userId == actorId)
        {
            return AdministrationCommandResult.Conflict(
                "You cannot block your own account; another administrator has to do it.");
        }

        var changes = new Dictionary<string, object?>();
        var now = DateTimeOffset.UtcNow;

        try
        {
            if (request.DisplayName.IsSet)
            {
                user.Rename(request.DisplayName.Value, actorId, now);
                changes["displayName"] = new { changed = true };
            }

            if (request.Email.IsSet)
            {
                user.ChangeEmail(request.Email.Value, actorId, now);
                changes["email"] = new { changed = true };
            }

            if (request.IsBlocked.IsSet)
            {
                var blocked = request.IsBlocked.Value ?? false;
                changes["isBlocked"] = new { from = user.IsBlocked, to = blocked };
                user.SetBlocked(blocked, actorId, now);
            }

            if (request.ExpiresOn.IsSet)
            {
                changes["expiresOn"] = new { from = user.ExpiresOn, to = request.ExpiresOn.Value };
                user.SetExpiry(request.ExpiresOn.Value, actorId, now);
            }

            if (request.Phone.IsSet || request.CompanyId.IsSet)
            {
                var phone = request.Phone.IsSet ? request.Phone.Value : user.Profile?.Phone;
                var companyId = request.CompanyId.IsSet ? request.CompanyId.Value : user.Profile?.CompanyId;

                user.SetProfile(phone, companyId, actorId, now);

                if (request.Phone.IsSet)
                {
                    changes["phone"] = new { changed = true };
                }

                if (request.CompanyId.IsSet)
                {
                    changes["companyId"] = new { to = companyId };
                }
            }
        }
        catch (AdministrationRuleException rule)
        {
            return AdministrationCommandResult.FromRule(rule);
        }

        if (changes.Count == 0)
        {
            return AdministrationCommandResult.Ok(
                AdministrationResponses.Detail(
                    user, await CreateUserHandler.CompanyNameAsync(store, user, cancellationToken).ConfigureAwait(false)));
        }

        audit.Record("user_account", user.Id.ToString(), "updated", actorId, changes);

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return AdministrationCommandResult.Ok(
            AdministrationResponses.Detail(
                user, await CreateUserHandler.CompanyNameAsync(store, user, cancellationToken).ConfigureAwait(false)));
    }
}

/// <summary>
/// Replaces the role set, refusing a change that would leave the caller without an administrative role
/// (T13 AC 3: self-lockout is unrecoverable without database access, so it is prevented rather than
/// fixed).
/// </summary>
public sealed class ReplaceUserRolesHandler(IAdministrationStore store, IAuditTrail audit)
{
    private const string AdministrativeCapability = "user.manage";

    public async Task<AdministrationCommandResult> HandleAsync(
        Guid userId,
        ReplaceUserRolesRequest request,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await store.FindAsync(userId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return AdministrationCommandResult.NotFound($"No user with id '{userId}' exists.");
        }

        var roles = await store.RolesAsync(cancellationToken).ConfigureAwait(false);

        if (!CreateUserHandler.TryResolveRoles(roles, request.RoleKeys, out var roleIds, out var unknown))
        {
            return AdministrationCommandResult.Validation($"Unknown role: '{unknown}'.");
        }

        if (userId == actorId)
        {
            var keepsAdministration = roles
                .Where(role => roleIds.Contains(role.Id))
                .Any(role => role.Capabilities.Any(grant =>
                    string.Equals(grant.Capability?.Key, AdministrativeCapability, StringComparison.Ordinal)));

            if (!keepsAdministration)
            {
                return AdministrationCommandResult.Conflict(
                    "That change would remove your own last administrative role, which cannot be undone " +
                    "without database access.");
            }
        }

        var before = user.RoleKeys;
        var now = DateTimeOffset.UtcNow;

        user.ReplaceRoles(roleIds, actorId, now);

        audit.Record(
            "user_account",
            user.Id.ToString(),
            "updated",
            actorId,
            new Dictionary<string, object?> { ["roles"] = new { from = before, to = user.RoleKeys } });

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return AdministrationCommandResult.Ok(
            AdministrationResponses.Detail(
                user, await CreateUserHandler.CompanyNameAsync(store, user, cancellationToken).ConfigureAwait(false)));
    }
}

public sealed class DeleteUserHandler(IAdministrationStore store, IAuditTrail audit)
{
    public async Task<AdministrationCommandResult> HandleAsync(
        Guid userId,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var user = await store.FindAsync(userId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return AdministrationCommandResult.NotFound($"No user with id '{userId}' exists.");
        }

        if (userId == actorId)
        {
            return AdministrationCommandResult.Conflict(
                "You cannot delete your own account; another administrator has to do it.");
        }

        store.Remove(user);

        audit.Record(
            "user_account",
            user.Id.ToString(),
            "deleted",
            actorId,
            new { roles = user.RoleKeys });

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return AdministrationCommandResult.Deleted();
    }
}
