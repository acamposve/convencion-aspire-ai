using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Convenciones.Identity;

/// <summary>
/// Reads the caller's session straight from PostgreSQL, with row-level security applied.
/// </summary>
/// <remarks>
/// <para>
/// Two things make this trustworthy rather than decorative. First, the claim the database reads is
/// the one this process set, not one the caller sent: <c>request.jwt.claims</c> is written from the
/// validated <c>sub</c> inside the transaction. Second, the transaction switches to the API role
/// before querying, so the RLS policies apply even when the configured login is a privileged
/// account. An API that connects as a superuser and merely *believes* RLS is protecting it is the
/// exact failure this guards against.
/// </para>
/// <para>
/// <c>SET LOCAL</c> is used throughout, so nothing leaks back into the pooled connection.
/// </para>
/// </remarks>
public sealed partial class PostgresSessionResolver : ISessionResolver
{
    private const string ApiRole = "app_api";

    private const string SessionSql = """
        select
            a.id,
            a.email,
            a.display_name,
            a.is_blocked,
            a.expires_on::timestamptz as expires_on,
            p.company_id,
            coalesce(
                (select array_agg(c.key order by c.key)
                 from platform.user_role ur
                 join platform.role_capability rc on rc.role_id = ur.role_id
                 join platform.capability c on c.id = rc.capability_id
                 where ur.user_id = a.id),
                '{}') as capabilities,
            platform.is_active_user() as is_active
        from platform.user_account a
        left join platform.user_profile p on p.user_id = a.id
        where a.id = platform.current_user_id()
        """;

    private readonly string _connectionString;

    public PostgresSessionResolver(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<SessionContext?> ResolveAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var claims = JsonSerializer.Serialize(new { sub = userId.ToString() });

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var session = await ReadAsync(connection, transaction, claims, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private static async Task<SessionContext?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string claims,
        CancellationToken cancellationToken)
    {
        // The role name is a constant, not configuration, so this cannot be used to inject SQL.
        if (!RoleNamePattern().IsMatch(ApiRole))
        {
            throw new InvalidOperationException($"'{ApiRole}' is not a safe role identifier.");
        }

        await using (var setRole = new NpgsqlCommand($"set local role {ApiRole}", connection, transaction))
        {
            await setRole.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var setClaims = new NpgsqlCommand(
            "select set_config('request.jwt.claims', @claims, true)", connection, transaction))
        {
            setClaims.Parameters.AddWithValue("claims", claims);
            await setClaims.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(SessionSql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SessionContext(
            UserId: reader.GetGuid(0),
            Email: reader.GetString(1),
            DisplayName: reader.IsDBNull(2) ? null : reader.GetString(2),
            CompanyId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
            IsActive: reader.GetBoolean(7),
            IsBlocked: reader.GetBoolean(3),
            ExpiresOn: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            Capabilities: (reader.GetFieldValue<string[]>(6) ?? []).ToHashSet(StringComparer.Ordinal));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex RoleNamePattern();
}
