using System.Data.Common;
using System.Text.Json;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Makes the database connection carry the authenticated caller's claims.
/// </summary>
/// <remarks>
/// <para>
/// The session resolver reads identity through its own short-lived transaction. The business
/// <c>DbContext</c> uses a pooled connection, so without this interceptor every RLS policy and every
/// capability guard trigger would see an empty <c>request.jwt.claims</c>: policies would deny reads
/// and guards such as <c>app.guard_agreement_publication</c> would refuse every publish, regardless
/// of what the API believed the caller could do.
/// </para>
/// <para>
/// The interceptor switches the connection to <c>app_api</c> as well, so the database policies apply
/// instead of being bypassed by a privileged login. Both values are written on <em>every</em> open:
/// a session sets the claims and the role, an operation without a session resets both. A pooled
/// connection therefore cannot carry one caller's claims into another caller's query, even if the
/// provider's own pool reset did not run.
/// </para>
/// </remarks>
public sealed class DatabaseSessionInterceptor(ISessionAccessor accessor) : DbConnectionInterceptor
{
    private const string ApiRole = "app_api";

    private const string ClaimsSql = "select set_config('request.jwt.claims', @claims, false)";

    private const string RoleSql = $"set role {ApiRole}";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Apply(connection);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return ApplyAsync(connection, cancellationToken);
    }

    private void Apply(DbConnection connection)
    {
        var session = accessor.Session;

        Execute(connection, ClaimsSql, session is null ? string.Empty : Claims(session));
        Execute(connection, session is null ? "reset role" : RoleSql, null);
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var session = accessor.Session;

        await ExecuteAsync(
            connection,
            ClaimsSql,
            session is null ? string.Empty : Claims(session),
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, session is null ? "reset role" : RoleSql, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Claims(SessionContext session) =>
        JsonSerializer.Serialize(new
        {
            sub = session.UserId.ToString(),
            role = "authenticated",
        });

    private static void Execute(DbConnection connection, string sql, string? claims)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (claims is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "claims";
            parameter.Value = claims;
            command.Parameters.Add(parameter);
        }

        command.ExecuteNonQuery();
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        string? claims,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (claims is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "claims";
            parameter.Value = claims;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
