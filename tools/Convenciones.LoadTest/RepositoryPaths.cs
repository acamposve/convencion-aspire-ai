using System;
using System.IO;
using System.Linq;

namespace Convenciones.LoadTest;

/// <summary>
/// Locates the repository checkout and the artifacts the harness must not guess.
/// </summary>
public static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string Migrations => Path.Combine(Root, "supabase", "migrations");

    public static string Seed => Path.Combine(Root, "supabase", "seed.sql");

    /// <summary>The built API assembly (Release preferred, Debug accepted for local runs).</summary>
    public static string ApiAssembly()
    {
        var candidates = new[]
        {
            Path.Combine(Root, "src", "Convenciones.Api", "bin", "Release", "net10.0", "Convenciones.Api.dll"),
            Path.Combine(Root, "src", "Convenciones.Api", "bin", "Debug", "net10.0", "Convenciones.Api.dll"),
        };

        foreach (var candidate in candidates.Where(File.Exists))
        {
            return candidate;
        }

        throw new InvalidOperationException(
            "Convenciones.Api.dll was not found. Build the solution in Release first: " +
            "'dotnet build Convenciones.slnx -c Release'.");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Convenciones.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Convenciones.slnx was not found above '{AppContext.BaseDirectory}'. " +
            "The load harness needs a repository checkout.");
    }
}
