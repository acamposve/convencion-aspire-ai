using System;
using System.IO;
using System.Threading.Tasks;
using Convenciones.Migrator;
using Npgsql;

// Applies the schema and reference data to the database Aspire provisioned, then exits.
// It runs as an Aspire resource that the API waits for, so `dotnet run` on the AppHost produces a
// working stack rather than an API pointing at an empty database.

var connectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{SqlMigrationRunner.ConnectionStringName}");
if (string.IsNullOrWhiteSpace(connectionString))
{
    // The deployment passes the parts, not a pre-assembled string: Npgsql escapes the password
    // correctly instead of relying on env-file quoting.
    var host = Environment.GetEnvironmentVariable("Database__Host");
    var user = Environment.GetEnvironmentVariable("Database__User");
    var password = Environment.GetEnvironmentVariable("Database__Password");
    if (!string.IsNullOrWhiteSpace(host)
        && !string.IsNullOrWhiteSpace(user)
        && !string.IsNullOrWhiteSpace(password))
    {
        _ = int.TryParse(Environment.GetEnvironmentVariable("Database__Port"), out var port);
        connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port == 0 ? 5432 : port,
            Database = Environment.GetEnvironmentVariable("Database__Name") ?? "postgres",
            Username = user,
            Password = password,
            SslMode = SslMode.Require,
        }.ConnectionString;
    }
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        $"Connection string 'ConnectionStrings__{SqlMigrationRunner.ConnectionStringName}' is not set. " +
        "Set it, or provide Database__Host, Database__User and Database__Password.");
    return 2;
}

var migrationsPath = Environment.GetEnvironmentVariable("MIGRATIONS_PATH");
if (string.IsNullOrWhiteSpace(migrationsPath) || !Directory.Exists(migrationsPath))
{
    Console.Error.WriteLine($"MIGRATIONS_PATH is not set or does not exist: '{migrationsPath}'.");
    return 2;
}

try
{
    var applied = await SqlMigrationRunner.ApplyAsync(connectionString, migrationsPath);

    var seedFile = Path.Combine(Path.GetDirectoryName(migrationsPath)!, "seed.sql");
    await SqlMigrationRunner.ApplySeedAsync(connectionString, seedFile);

    Console.WriteLine($"Applied {applied} migration(s) and the reference seed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
