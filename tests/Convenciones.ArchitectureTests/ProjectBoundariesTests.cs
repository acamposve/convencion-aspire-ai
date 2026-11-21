using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Convenciones.ArchitectureTests;

/// <summary>
/// The constitution (Art. IV.2) requires dependency direction and
/// <c>solution-architecture.md</c> §3.4 states the module dependency rules; §11 says an architecture
/// test enforces them. This is that test.
/// </summary>
/// <remarks>
/// The tests read the real project graph from the repository instead of inspecting assemblies, so they
/// verify what the build actually references and cannot drift from a stale copy. They are file-system
/// tests on purpose: they need the checkout, which is exactly the artifact CI builds.
/// </remarks>
public sealed class ProjectBoundariesTests
{
    private const string SolutionFileName = "Convenciones.slnx";

    private static readonly string[] BusinessModules =
    [
        "Convenciones.Administration",
        "Convenciones.Agreements",
        "Convenciones.Clauses",
        "Convenciones.Comparison",
        "Convenciones.Legal",
        "Convenciones.Negotiation",
        "Convenciones.Publishing",
    ];

    private static readonly string[] PlatformModules = ["Convenciones.Documents"];

    private static readonly string[] Kernel = ["Convenciones.Catalog", "Convenciones.Identity"];

    private static readonly string[] Composition = ["Convenciones.Api", "Convenciones.Worker", "Convenciones.Backup"];

    /// <summary>
    /// The whole permitted graph, one entry per source project. A reference that is not listed here,
    /// or a listed reference that disappeared, fails <see cref="Project_references_match_the_permitted_graph"/>;
    /// a new project fails <see cref="Every_source_project_is_classified"/>. Both are review points,
    /// not accidents: §3.4 is where a new edge must be justified first.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> PermittedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Convenciones.Administration"] = ["Convenciones.Catalog"],
            ["Convenciones.Agreements"] = ["Convenciones.Catalog"],
            ["Convenciones.Ai"] = [],
            ["Convenciones.Api"] =
            [
                "Convenciones.Administration",
                "Convenciones.Agreements",
                "Convenciones.Catalog",
                "Convenciones.Clauses",
                "Convenciones.Comparison",
                "Convenciones.Documents",
                "Convenciones.Identity",
                "Convenciones.Legal",
                "Convenciones.Negotiation",
                "Convenciones.Publishing",
                "Convenciones.ServiceDefaults",
            ],
            ["Convenciones.AppHost"] = ["Convenciones.Api", "Convenciones.Migrator"],
            ["Convenciones.Backup"] = ["Convenciones.Migrator"],
            ["Convenciones.Catalog"] = ["Convenciones.Identity"],
            ["Convenciones.Clauses"] = ["Convenciones.Catalog"],
            ["Convenciones.Comparison"] = ["Convenciones.Agreements", "Convenciones.Catalog"],
            ["Convenciones.Documents"] = ["Convenciones.Catalog"],
            ["Convenciones.Identity"] = [],
            ["Convenciones.Legal"] = ["Convenciones.Catalog"],
            ["Convenciones.Migrator"] = [],
            ["Convenciones.Negotiation"] = ["Convenciones.Catalog"],
            ["Convenciones.Publishing"] = ["Convenciones.Agreements", "Convenciones.Catalog"],
            ["Convenciones.ServiceDefaults"] = [],
            ["Convenciones.Worker"] = ["Convenciones.Ai", "Convenciones.Clauses", "Convenciones.Documents"],
        };

    private static readonly Regex NamespacePattern = new(
        @"^\s*namespace\s+([A-Za-z0-9_.]+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void Every_source_project_is_classified()
    {
        var actual = SourceGraph().Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var classified = PermittedReferences.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        actual.ShouldBe(
            classified,
            "a source project must be classified in PermittedReferences — with its rules recorded in " +
            "solution-architecture.md §3.4 — before it can merge");
    }

    [Fact]
    public void Project_references_match_the_permitted_graph()
    {
        var problems = new List<string>();

        foreach (var (project, references) in SourceGraph())
        {
            if (!PermittedReferences.TryGetValue(project, out var permitted))
            {
                problems.Add($"{project}: not classified");
                continue;
            }

            var unexpected = references.Except(permitted, StringComparer.Ordinal).ToArray();
            var missing = permitted.Except(references, StringComparer.Ordinal).ToArray();

            if (unexpected.Length > 0)
            {
                problems.Add($"{project} references {Format(unexpected)}, outside its permitted graph");
            }

            if (missing.Length > 0)
            {
                problems.Add($"{project} no longer references {Format(missing)}; update PermittedReferences");
            }
        }

        problems.ShouldBeEmpty(
            "a project reference is a boundary decision: justify it in §3.4 and update the permitted graph in the same change");
    }

    [Fact]
    public void Modules_do_not_reference_composition_projects()
    {
        var forbidden = Composition.Append("Convenciones.AppHost").ToArray();

        foreach (var module in BusinessModules.Concat(PlatformModules).Concat(Kernel))
        {
            SourceGraph()[module].Intersect(forbidden, StringComparer.Ordinal)
                .ShouldBeEmpty($"{module} must not depend on a composition project ({Format(forbidden)})");
        }
    }

    [Fact]
    public void The_kernel_does_not_reference_modules()
    {
        var modules = BusinessModules.Concat(PlatformModules).ToArray();

        foreach (var kernel in Kernel)
        {
            SourceGraph()[kernel].Intersect(modules, StringComparer.Ordinal)
                .ShouldBeEmpty($"{kernel} is a kernel project: the arrow points from modules to the kernel, never back");
        }
    }

    [Fact]
    public void Modules_reference_the_kernel_or_a_documented_peer_only()
    {
        var documentedPeers = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Convenciones.Comparison"] = ["Convenciones.Agreements"],
            ["Convenciones.Publishing"] = ["Convenciones.Agreements"],
        };

        foreach (var module in BusinessModules.Concat(PlatformModules))
        {
            var permitted = Kernel.Concat(documentedPeers.GetValueOrDefault(module, [])).ToArray();
            var unexpected = SourceGraph()[module].Except(permitted, StringComparer.Ordinal).ToArray();

            unexpected.ShouldBeEmpty(
                $"{module} may reference only the kernel and its documented peer, currently {Format(permitted)}");
        }
    }

    [Fact]
    public void The_worker_references_only_the_modules_it_executes()
    {
        SourceGraph()["Convenciones.Worker"].ShouldBe(
            ["Convenciones.Ai", "Convenciones.Clauses", "Convenciones.Documents"],
            ignoreOrder: true,
            customMessage: "the worker executes the extraction and classification pipeline; a new stage is a reviewed dependency");
    }

    [Fact]
    public void The_api_references_modules_the_kernel_and_service_defaults_only()
    {
        var permitted = BusinessModules.Concat(PlatformModules).Concat(Kernel)
            .Append("Convenciones.ServiceDefaults")
            .ToArray();

        SourceGraph()["Convenciones.Api"].Except(permitted, StringComparer.Ordinal)
            .ShouldBeEmpty("the API is the composition root; anything else belongs behind a module boundary");
    }

    [Fact]
    public void Hosts_do_not_reference_other_hosts()
    {
        var graph = SourceGraph();
        var forbidden = Composition.Concat(["Convenciones.AppHost"]).ToArray();

        foreach (var host in Composition)
        {
            graph[host].Intersect(forbidden, StringComparer.Ordinal)
                .ShouldBeEmpty($"{host} must not reference another host; the AppHost is the only orchestrator");
        }
    }

    [Fact]
    public void Project_references_are_acyclic()
    {
        var graph = SourceGraph();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        void Visit(string project)
        {
            if (visited.Contains(project))
            {
                return;
            }

            if (!visiting.Add(project))
            {
                var cycle = path.SkipWhile(step => step != project).Append(project);
                throw new InvalidOperationException($"Project reference cycle: {string.Join(" -> ", cycle)}");
            }

            path.Add(project);
            foreach (var reference in graph[project])
            {
                if (graph.ContainsKey(reference))
                {
                    Visit(reference);
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(project);
            visited.Add(project);
        }

        foreach (var project in graph.Keys)
        {
            Visit(project);
        }
    }

    [Fact]
    public void Every_declared_namespace_stays_inside_its_own_project()
    {
        var problems = new List<string>();

        foreach (var projectPath in SourceProjectFiles())
        {
            var project = Path.GetFileNameWithoutExtension(projectPath)!;
            var directory = Path.GetDirectoryName(projectPath)!;

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                {
                    continue;
                }

                var declared = DeclaredNamespace(File.ReadAllText(file));
                if (declared is null)
                {
                    // Top-level Program.cs has no namespace declaration; it belongs to its own project.
                    continue;
                }

                var insideProject =
                    declared.Equals(project, StringComparison.Ordinal) ||
                    declared.StartsWith(project + ".", StringComparison.Ordinal);

                if (!insideProject)
                {
                    problems.Add(
                        $"{Path.GetRelativePath(RepositoryRoot(), file)} declares '{declared}', " +
                        $"which is outside project '{project}'");
                }
            }
        }

        problems.ShouldBeEmpty("a type belongs to the project that owns its namespace, not to a neighbour");
    }

    // ------------------------------------------------------------------ helpers

    private static IReadOnlyDictionary<string, string[]> SourceGraph()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var projectPath in SourceProjectFiles())
        {
            graph[Path.GetFileNameWithoutExtension(projectPath)!] = ProjectReferences(projectPath);
        }

        return graph;
    }

    private static IEnumerable<string> SourceProjectFiles()
    {
        var sourceDirectory = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static string[] ProjectReferences(string projectPath)
    {
        return XDocument.Load(projectPath)
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            // MSBuild writes Windows separators even on Linux checkouts; normalise before the split.
            .Select(value => value!.Replace('\\', '/').Split('/')[^1])
            .Select(fileName => fileName.EndsWith(".csproj", StringComparison.Ordinal) ? fileName[..^".csproj".Length] : fileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? DeclaredNamespace(string source)
    {
        var match = NamespacePattern.Match(source);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsBuildOutput(string path)
    {
        var normalised = path.Replace('\\', '/');
        return normalised.Contains("/bin/", StringComparison.Ordinal) ||
               normalised.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"'{SolutionFileName}' was not found above '{AppContext.BaseDirectory}'; the architecture " +
            "tests need a repository checkout.");
    }

    private static string Format(IEnumerable<string> values) =>
        values.Any() ? string.Join(", ", values.OrderBy(value => value, StringComparer.Ordinal)) : "none";
}
