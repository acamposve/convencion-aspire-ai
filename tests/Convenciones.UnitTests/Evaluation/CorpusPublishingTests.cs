using Convenciones.Evaluation;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Evaluation;

/// <summary>
/// Publishing is the only path from reviews to ground truth. It must never invent a decision: no
/// anonymous adjudicator, no silently resolved disagreement, no stale page range.
/// </summary>
public sealed class CorpusPublishingTests
{
    private static readonly string[] Taxonomy = ["scope", "pricing", "liability", "other"];

    [Fact]
    public void Agreed_reviews_publish_a_reviewed_corpus()
    {
        var dataset = Dataset();
        var worksheet = Finalized(dataset, Answer(cats: ["pricing"], primary: "pricing"));

        var published = CorpusPublisher.Materialize(dataset, worksheet, "2");

        published.DatasetVersion.ShouldBe("2");
        published.Status.ShouldBe("reviewed");
        published.Reviewers.ShouldBe(["adjudicator-1"]);
        published.Documents.SelectMany(document => document.Clauses)
            .ShouldAllBe(clause => clause.Status == "reviewed" && clause.Reviewer == "adjudicator-1");
        published.Documents[0].Clauses[0].Categories.ShouldBe(["pricing"]);
    }

    [Fact]
    public void Unresolved_disagreement_is_refused()
    {
        var dataset = Dataset();
        var worksheet = Finalized(
            dataset,
            Answer(cats: ["pricing"], primary: "pricing"),
            b: Answer(cats: ["scope"], primary: "scope"));

        var error = Should.Throw<EvaluationInputException>(() =>
            CorpusPublisher.Materialize(dataset, worksheet, "2"));

        error.Message.ShouldContain("no finalCategories decision");
    }

    [Fact]
    public void Adjudicated_decision_is_applied()
    {
        var dataset = Dataset();
        var worksheet = Finalized(
            dataset,
            Answer(cats: ["pricing"], primary: "pricing"),
            b: Answer(cats: ["scope"], primary: "scope"));

        worksheet = worksheet with
        {
            Rows = worksheet.Rows.Select(row => row with
            {
                FinalCategories = ["pricing", "scope"],
                FinalPrimary = "pricing",
            }).ToList(),
        };

        var published = CorpusPublisher.Materialize(dataset, worksheet, "2");

        published.Documents[0].Clauses[0].Categories.ShouldBe(["pricing", "scope"]);
        published.Documents[0].Clauses[0].Primary.ShouldBe("pricing");
    }

    [Fact]
    public void Page_disagreement_requires_a_final_range()
    {
        var dataset = Dataset();
        var worksheet = Finalized(
            dataset,
            Answer(cats: ["pricing"], primary: "pricing", from: 1, to: 1),
            b: Answer(cats: ["pricing"], primary: "pricing", from: 2, to: 2));

        var error = Should.Throw<EvaluationInputException>(() =>
            CorpusPublisher.Materialize(dataset, worksheet, "2"));

        error.Message.ShouldContain("different page ranges");
    }

    [Fact]
    public void Final_page_range_overrides_both_reviewers()
    {
        var dataset = Dataset();
        var worksheet = Finalized(
            dataset,
            Answer(cats: ["pricing"], primary: "pricing", from: 1, to: 1),
            b: Answer(cats: ["pricing"], primary: "pricing", from: 2, to: 2));

        worksheet = worksheet with
        {
            Rows = worksheet.Rows.Select(row => row with { FinalPageFrom = 2, FinalPageTo = 3 }).ToList(),
        };

        var published = CorpusPublisher.Materialize(dataset, worksheet, "2");

        published.Documents[0].Clauses[0].PageFrom.ShouldBe(2);
        published.Documents[0].Clauses[0].PageTo.ShouldBe(3);
    }

    [Fact]
    public void Anonymous_adjudication_is_refused()
    {
        var dataset = Dataset();
        var worksheet = Finalized(dataset, Answer(cats: ["pricing"], primary: "pricing"));
        worksheet = worksheet with
        {
            Rows = worksheet.Rows.Select(row => row with { Adjudicator = null }).ToList(),
        };

        var error = Should.Throw<EvaluationInputException>(() =>
            CorpusPublisher.Materialize(dataset, worksheet, "2"));

        error.Message.ShouldContain("no adjudicator");
    }

    [Fact]
    public void Version_must_change()
    {
        var dataset = Dataset();
        var worksheet = Finalized(dataset, Answer(cats: ["pricing"], primary: "pricing"));

        Should.Throw<EvaluationInputException>(() => CorpusPublisher.Materialize(dataset, worksheet, "1"));
    }

    [Fact]
    public void Published_corpus_passes_the_standard_validation()
    {
        var dataset = Dataset();
        var worksheet = Finalized(dataset, Answer(cats: ["pricing"], primary: "pricing"));

        var published = CorpusPublisher.Materialize(dataset, worksheet, "2");

        Should.NotThrow(() => EvaluationValidator.ValidateDataset(published));
        CorpusCoverageReport.Build(published).ReviewedClauses.ShouldBe(published.Documents.Sum(d => d.Clauses.Count));
    }

    private static (string[] Cats, string? Primary, int From, int To) Answer(
        string[] cats,
        string? primary,
        int from = 1,
        int to = 1) => (cats, primary, from, to);

    private static AdjudicationWorksheet Finalized(
        EvaluationDataset dataset,
        (string[] Cats, string? Primary, int From, int To) a,
        (string[] Cats, string? Primary, int From, int To)? b = null)
    {
        var reviewerB = b ?? a;
        var packageA = Build(dataset, "A", "reviewer-a", a);
        var packageB = Build(dataset, "B", "reviewer-b", reviewerB);
        var worksheet = ReviewWorkflow.Adjudicate(dataset, packageA, packageB);

        return worksheet with
        {
            Rows = worksheet.Rows.Select(row => row with { Adjudicator = "adjudicator-1" }).ToList(),
        };
    }

    private static ReviewPackage Build(
        EvaluationDataset dataset,
        string slot,
        string identity,
        (string[] Cats, string? Primary, int From, int To) answer)
    {
        var package = ReviewWorkflow.ForReviewer(dataset, slot);

        return package with
        {
            ReviewerIdentity = identity,
            Clauses = package.Clauses.Select(clause => clause with
            {
                Categories = answer.Cats,
                Primary = answer.Primary,
                PageFrom = answer.From,
                PageTo = answer.To,
            }).ToList(),
        };
    }

    private static EvaluationDataset Dataset() =>
        new(
            "1",
            "unit-publish",
            "extraction/v2",
            "20260109000100",
            [
                new CorpusDocument("doc-1", "doc-1",
                [
                    new AnnotatedClauseRecord("c1", "Cláusula de precio.", null, null, 1, 1, ["pricing"], "pricing", "draft", "annotator-1"),
                    new AnnotatedClauseRecord("c2", "Cláusula de alcance.", null, null, 1, 1, ["scope"], "scope", "draft", "annotator-1"),
                ], "labour"),
            ],
            SchemaVersion: 2,
            Status: "draft",
            TaxonomyKeys: Taxonomy,
            Annotators: ["annotator-1"],
            Reviewers: []);
}
