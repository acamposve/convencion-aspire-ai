using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convenciones.Migrator;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Convenciones.LoadTest;

/// <summary>
/// Disposable PostgreSQL 17 container with the real migrations and seed.
/// </summary>
/// <remarks>
/// Three connection strings are handed out so the pool sizes are explicit and the database's
/// connection ceiling is not a hidden variable: API 60, harness 10, worker 5, all below the default
/// <c>max_connections = 100</c>. The report records the pool sizes.
/// </remarks>
public sealed class PostgresHarness : IAsyncDisposable
{
    public const string Image = "postgres:17-alpine";
    public const int ApiPoolSize = 60;
    public const int HarnessPoolSize = 10;
    public const int WorkerPoolSize = 5;

    private PostgreSqlContainer? _container;

    public string ApiConnectionString { get; private set; } = string.Empty;

    public string HarnessConnectionString { get; private set; } = string.Empty;

    public string WorkerConnectionString { get; private set; } = string.Empty;

    public static int MaxConnections => 100;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("convenciones")
            .WithCleanUp(true)
            .Build();

        await _container.StartAsync(cancellationToken).ConfigureAwait(false);

        var baseConnectionString = _container.GetConnectionString();
        ApiConnectionString = WithPool(baseConnectionString, ApiPoolSize);
        HarnessConnectionString = WithPool(baseConnectionString, HarnessPoolSize);
        WorkerConnectionString = WithPool(baseConnectionString, WorkerPoolSize);

        await SqlMigrationRunner.ApplyAsync(HarnessConnectionString, RepositoryPaths.Migrations)
            .ConfigureAwait(false);
        await SqlMigrationRunner.ApplySeedAsync(HarnessConnectionString, RepositoryPaths.Seed)
            .ConfigureAwait(false);

        await AnalyzeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fresh planner statistics. The requirement is explicit: ANALYZE before measuring.</summary>
    public Task AnalyzeAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("analyze;", cancellationToken);

    public async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(HarnessConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(sql, connection, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken cancellationToken = default, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(HarnessConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(sql, connection, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<List<T>> QueryAsync<T>(
        string sql,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(HarnessConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(sql, connection, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>Connections currently open in this database, whatever pool they came from.</summary>
    public Task<int> OpenConnectionCountAsync(CancellationToken cancellationToken = default) =>
        ScalarAsync<int>(
            "select count(*)::int from pg_stat_activity where datname = current_database();",
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string WithPool(string connectionString, int poolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = poolSize,
            MinPoolSize = 0,
            Timeout = 30,
            CommandTimeout = 120,
        };

        return builder.ConnectionString;
    }
}
