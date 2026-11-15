using Convenciones.Evaluation;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Evaluation;

/// <summary>
/// Rules that make "draft" and "reviewed" mean something. Without them a corpus could claim review
/// status it never had, or use a category that does not exist in the taxonomy of record.
/// </summary>
public sealed class CorpusValidationTests
{
    private static readonly string[] Taxonomy =
    [
        "parties", "scope", "pricing", "duration", "termination", "confidentiality",
        "data_protection", "ip", "liability", "warranties", "disputes", "governing_law",
        "force_majeure", "other",
    ];

    [Fact]
    public void Valid_draft_corpus_passes_and_reports_coverage()
    {
        var dataset = Dataset(Clause("c1", ["pricing"], "pricing"));
        var coverage = CorpusCoverageReport.Build(dataset);

        coverage.Documents.ShouldBe(1);
        coverage.Clauses.ShouldBe(1);
        coverage.DraftClauses.ShouldBe(1);
        coverage.ReviewedClauses.ShouldBe(0);
        coverage.Categories.Count.ShouldBe(Taxonomy.Length);
        coverage.Categories.Single(entry => entry.Category == "pricing").Clauses.ShouldBe(1);
        coverage.Categories.Single(entry => entry.Category == "ip").Clauses.ShouldBe(0);
    }

    [Fact]
    public void Category_outside_the_taxonomy_is_rejected()
    {
        var dataset = Dataset(Clause("c1", ["not_a_category"], "not_a_category"));

        var error = Should.Throw<EvaluationInputException>(() => EvaluationValidator.ValidateDataset(dataset));
        error.Message.ShouldContain("not in taxonomyKeys");
    }

    [Fact]
    public void Clause_without_an_annotator_is_a_missing_annotation()
    {
        var dataset = Dataset(Clause("c1", ["pricing"], "pricing", annotator: null));

        var error = Should.Throw<EvaluationInputException>(() => EvaluationValidator.ValidateDataset(dataset));
        error.Message.ShouldContain("missing annotation");
    }

    [Fact]
    public void Reviewed_clause_without_a_reviewer_is_rejected()
    {
        var dataset = Dataset(Clause("c1", ["pricing"], "pricing", status: "reviewed", reviewer: null));

        var error = Should.Throw<EvaluationInputException>(() => EvaluationValidator.ValidateDataset(dataset));
        error.Message.ShouldContain("names no reviewer");
    }

    [Fact]
    public void Reviewed_clause_with_an_open_disagreement_is_rejected()
    {
        var dataset = Dataset(Clause(
            "c1", ["pricing"], "pricing", status: "reviewed", reviewer: "r1",
            disagreement: "second reviewer preferred 'scope'"));

        var error = Should.Throw<EvaluationInputException>(() => EvaluationValidator.ValidateDataset(dataset));
        error.Message.ShouldContain("unresolved disagreement");
    }

    [Fact]
    public void Reviewed_corpus_cannot_contain_a_draft_clause()
    {
        var dataset = Dataset(
            [Clause("c1", ["pricing"], "pricing", status: "reviewed", reviewer: "r1")],
            corpusStatus: "reviewed",
            reviewers: ["r1"])
            with
            {
                Documents =
                [
                    new CorpusDocument("doc-1", "doc-1",
                    [
                        Clause("c1", ["pricing"], "pricing", status: "reviewed", reviewer: "r1"),
                        Clause("c2", ["scope"], "scope", status: "draft"),
                    ]),
                ],
            };

        var error = Should.Throw<EvaluationInputException>(() => EvaluationValidator.ValidateDataset(dataset));
        error.Message.ShouldContain("still 'draft'");
    }

    [Fact]
    public void Reviewed_corpus_must_name_its_reviewers()
    {
        var dataset = Dataset(
            [Clause("c1", ["pricing"], "pricing", status: "reviewed", reviewer: "r1")],
            corpusStatus: "reviewed",
            reviewers: []);

        var error = Should.Throw<EvaluationInputException>(() => EvaluationValidator.ValidateDataset(dataset));
        error.Message.ShouldContain("name its reviewers");
    }

    [Fact]
    public void Legacy_schema_one_corpus_still_validates()
    {
        // The D6 demo corpus predates provenance; it must remain loadable so old results reproduce.
        var legacy = new EvaluationDataset(
            "1", "legacy", "extraction/v1", "20260109000100",
            [new CorpusDocument("doc-1", "doc-1", [Clause("c1", ["pricing"], "pricing", annotator: null)])]);

        Should.NotThrow(() => EvaluationValidator.ValidateDataset(legacy));
    }

    [Fact]
    public void Uncovered_categories_are_visible_in_the_coverage_report()
    {
        var coverage = CorpusCoverageReport.Build(Dataset(Clause("c1", ["pricing"], "pricing")));
        var uncovered = coverage.Categories.Where(entry => entry.Clauses == 0).ToList();

        uncovered.Count.ShouldBe(Taxonomy.Length - 1);
        uncovered.ShouldNotContain(entry => entry.Category == "pricing");
    }

    private static EvaluationDataset Dataset(
        params AnnotatedClauseRecord[] clauses) =>
        Dataset(clauses, "draft", []);

    private static EvaluationDataset Dataset(
        AnnotatedClauseRecord[] clauses,
        string corpusStatus,
        IReadOnlyList<string> reviewers) =>
        new(
            "1",
            "unit-v2",
            "extraction/v2",
            "20260109000100",
            [new CorpusDocument("doc-1", "doc-1", clauses, "labour")],
            SchemaVersion: 2,
            Status: corpusStatus,
            TaxonomyKeys: Taxonomy,
            Annotators: ["annotator-1"],
            Reviewers: reviewers);

    private static AnnotatedClauseRecord Clause(
        string id,
        IReadOnlyList<string> categories,
        string? primary,
        string status = "draft",
        string? annotator = "annotator-1",
        string? reviewer = null,
        string? disagreement = null) =>
        new(id, "Cláusula de prueba.", null, null, 1, 1, categories, primary, status, annotator, reviewer, null, disagreement);
}
