using System;
using System.IO;
using System.Threading.Tasks;
using Convenciones.Migrator;

// Applies the schema and reference data to the database Aspire provisioned, then exits.
// It runs as an Aspire resource that the API waits for, so `dotnet run` on the AppHost produces a
// working stack rather than an API pointing at an empty database.

var connectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{SqlMigrationRunner.ConnectionStringName}");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        $"Connection string 'ConnectionStrings__{SqlMigrationRunner.ConnectionStringName}' is not set. " +
        "This tool is normally launched by the Aspire AppHost, which injects it.");
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
