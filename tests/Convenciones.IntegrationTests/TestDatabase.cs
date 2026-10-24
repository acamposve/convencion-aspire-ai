using System;
using System.IO;
using System.Threading.Tasks;
using Convenciones.Migrator;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Test-side helpers that locate the repository and apply the real schema.
/// </summary>
/// <remarks>
/// The migrations are applied through <see cref="SqlMigrationRunner"/> — the exact code the Aspire
/// migrator resource runs — so the schema a test asserts against is byte-identical to the one a
/// developer gets from <c>dotnet run</c>.
/// </remarks>
internal static class TestDatabase
{
    public static Task ApplyAsync(string connectionString) =>
        SqlMigrationRunner.ApplyAsync(connectionString, MigrationsDirectory());

    public static Task ApplySeedAsync(string connectionString) =>
        SqlMigrationRunner.ApplySeedAsync(connectionString, Path.Combine(RepositoryRoot(), "supabase", "seed.sql"));

    public static string MigrationsDirectory() =>
        Path.Combine(RepositoryRoot(), "supabase", "migrations");

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (SolutionExists(directory.FullName))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Convenciones.slnx/.sln not found above the test directory.");
    }

    /// <summary>.NET 10 emits a .slnx by default; older solutions use .sln. Accept both.</summary>
    private static bool SolutionExists(string directory) =>
        File.Exists(Path.Combine(directory, "Convenciones.slnx")) ||
        File.Exists(Path.Combine(directory, "Convenciones.sln"));
}
