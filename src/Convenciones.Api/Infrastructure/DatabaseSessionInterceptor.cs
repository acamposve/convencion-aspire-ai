using System.Data.Common;
using System.Text.Json;
using Convenciones.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Convenciones.Api.Infrastructure;

/// <summary>
/// Makes the database connection carry the caller's claims and the role the request must run as.
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
/// The interceptor switches the connection's role as well, so the database policies apply instead of
/// being bypassed by a privileged login. Both values are written on <em>every</em> open: a session sets
/// the claims and <c>app_api</c>, a public request sets <c>anon</c>, and anything else resets both. A
/// pooled connection therefore cannot carry one caller's identity into another caller's query.
/// </para>
/// <para>
/// The <c>anon</c> role is opt-in through <see cref="PublicDataAttribute"/>: an anonymous endpoint that
/// did not declare public data (the bootstrap endpoint, for example) keeps the login's privileges. That
/// distinction matters, because "anonymous" describes the caller, not what the request may read.
/// </para>
/// </remarks>
public sealed class DatabaseSessionInterceptor(ISessionAccessor accessor) : DbConnectionInterceptor
{
    private const string ApiRole = "app_api";

    private const string PublicRole = "anon";

    private const string ClaimsSql = "select set_config('request.jwt.claims', @claims, false)";

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
        Execute(connection, RoleCommand(session, accessor.DatabaseRole), null);
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var session = accessor.Session;

        await ExecuteAsync(
            connection,
            ClaimsSql,
            session is null ? string.Empty : Claims(session),
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            RoleCommand(session, accessor.DatabaseRole),
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An authenticated session runs as <c>app_api</c>; an anonymous request that declared public data
    /// runs as <c>anon</c>, so the public policies are the only thing between it and a draft; anything
    /// else resets the role.
    /// </summary>
    private static string RoleCommand(SessionContext? session, RequestDatabaseRole requestRole) =>
        session is not null
            ? $"set role {ApiRole}"
            : requestRole switch
            {
                RequestDatabaseRole.Api => $"set role {ApiRole}",
                RequestDatabaseRole.Anonymous => $"set role {PublicRole}",
                _ => "reset role",
            };

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
