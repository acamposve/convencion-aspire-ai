using System;
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

/// <summary>
/// Guards the migration contract: files are ordered, versioned and uniquely named.
/// </summary>
/// <remarks>
/// This is an infrastructure invariant, not a business rule. It earns a test because the ordering
/// of the migration files *is* the deployment order: a mis-named file silently applies a schema
/// change before its dependency and fails only on a from-scratch build — exactly the class of
/// defect that surfaced during Phase 4 validation.
/// </remarks>
public sealed class MigrationFileTests
{
    private static readonly string[] ExpectedOrder =
    [
        "20260101000000_bootstrap.sql",
        "20260101000100_platform_identity.sql",
        "20260101000200_platform_documents_audit.sql",
        "20260101000300_app_catalog.sql",
        "20260101000400_app_comparison.sql",
        "20260101000500_app_legal.sql",
        "20260101000600_app_agreements.sql",
        "20260101000700_app_negotiation.sql",
        "20260101000800_app_publishing.sql",
        "20260101000900_grants.sql",
        "20260101001000_rls_policies.sql",
        "20260102000000_align_product_decisions.sql",
        "20260102000100_document_processing.sql",
    ];

    [Fact]
    public void Migration_files_are_ordered_and_named_consistently()
    {
        var files = MigrationFiles()
            .Select(Path.GetFileName)
            .ToArray();

        files.ShouldBe(ExpectedOrder, ignoreOrder: false);
    }

    [Fact]
    public void Every_migration_name_follows_the_timestamped_convention()
    {
        foreach (var name in MigrationFiles().Select(Path.GetFileName))
        {
            name.ShouldNotBeNull();
            name.ShouldMatch(@"^\d{14}_[a-z0-9_]+\.sql$");
        }
    }

    [Fact]
    public void Migration_prefixes_are_unique_so_ordering_is_deterministic()
    {
        var prefixes = MigrationFiles()
            .Select(f => Path.GetFileName(f)![..14])
            .ToArray();

        prefixes.Distinct(StringComparer.Ordinal).Count().ShouldBe(prefixes.Length);
    }

    private static string[] MigrationFiles()
    {
        var directory = Path.Combine(RepositoryRoot(), "supabase", "migrations");

        Directory.Exists(directory).ShouldBeTrue($"migrations directory not found: {directory}");

        return Directory.GetFiles(directory, "*.sql").OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Walks up from the test binary until the solution file is found.</summary>
    private static string RepositoryRoot()
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
