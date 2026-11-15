using Convenciones.Evaluation;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Evaluation;

/// <summary>
/// The review round only means something if the exports are blind, the imports are validated, and no
/// tooling ever decides a disagreement on a human's behalf.
/// </summary>
public sealed class ReviewWorkflowTests
{
    private static readonly string[] Taxonomy = ["scope", "pricing", "liability", "other"];

    [Fact]
    public void Export_is_blind_and_contains_every_clause()
    {
        var dataset = Dataset();
        var package = ReviewWorkflow.ForReviewer(dataset, "A");

        package.Clauses.Count.ShouldBe(2);
        package.ReviewerSlot.ShouldBe("A");
        package.Status.ShouldBe("blank");
        package.ReviewerIdentity.ShouldBeNull();
        package.Clauses.ShouldAllBe(clause => clause.Categories.Count == 0 && clause.Primary == null);
        package.Clauses.ShouldAllBe(clause => clause.Text.Length > 0);
    }

    [Fact]
    public void Reviewer_exports_do_not_carry_each_others_answers()
    {
        var dataset = Dataset();
        var a = ReviewWorkflow.ForReviewer(dataset, "A");
        var b = ReviewWorkflow.ForReviewer(dataset, "B");

        a.PackageId.ShouldNotBe(b.PackageId);
        a.Clauses.Select(clause => clause.ClauseId).ShouldBe(b.Clauses.Select(clause => clause.ClauseId));
        b.Clauses.ShouldAllBe(clause => clause.Categories.Count == 0 && clause.Primary == null);
        b.ReviewerIdentity.ShouldBeNull();
    }

    [Fact]
    public void Missing_clause_is_reported()
    {
        var dataset = Dataset();
        var submission = Submitted(dataset, "A", clauseFilter: clause => clause.ClauseId == "c1");

        var problems = ReviewWorkflow.ValidateSubmission(dataset, submission);

        problems.ShouldContain(problem => problem.Contains("'c2' has no answer"));
    }

    [Fact]
    public void Duplicate_and_unknown_clause_ids_are_reported()
    {
        var dataset = Dataset();
        var submission = Submitted(dataset, "A") with
        {
            Clauses =
            [
                .. Submitted(dataset, "A").Clauses,
                new ReviewClause("c1", "doc-1", "labour", null, null, "duplicada", 1, 1, ["scope"], "scope", false, null),
            ],
        };

        var problems = ReviewWorkflow.ValidateSubmission(dataset, submission);

        problems.ShouldContain(problem => problem.Contains("Duplicate clause id 'c1'"));
    }

    [Fact]
    public void Unknown_taxonomy_category_is_reported()
    {
        var dataset = Dataset();
        var submission = Submitted(dataset, "A") with
        {
            Clauses =
            [
                new ReviewClause("c1", "doc-1", "labour", null, null, "texto", 1, 1, ["working_conditions"], null, false, null),
                .. Submitted(dataset, "A").Clauses.Skip(1),
            ],
        };

        var problems = ReviewWorkflow.ValidateSubmission(dataset, submission);

        problems.ShouldContain(problem => problem.Contains("unknown category 'working_conditions'"));
    }

    [Fact]
    public void Primary_outside_the_selection_is_reported()
    {
        var dataset = Dataset();
        var submission = Submitted(dataset, "A") with
        {
            Clauses =
            [
                new ReviewClause("c1", "doc-1", "labour", null, null, "texto", 1, 1, ["scope"], "pricing", false, null),
                .. Submitted(dataset, "A").Clauses.Skip(1),
            ],
        };

        var problems = ReviewWorkflow.ValidateSubmission(dataset, submission);

        problems.ShouldContain(problem => problem.Contains("primary category outside its selection"));
    }

    [Fact]
    public void Anonymous_submission_is_reported()
    {
        var dataset = Dataset();
        var submission = Submitted(dataset, "A") with { ReviewerIdentity = null };

        var problems = ReviewWorkflow.ValidateSubmission(dataset, submission);

        problems.ShouldContain(problem => problem.Contains("reviewerIdentity is required"));
    }

    [Fact]
    public void Import_refuses_to_overwrite_an_existing_review()
    {
        var dataset = Dataset();
        var path = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}.json");
        try
        {
            ReviewWorkflow.Import(dataset, Submitted(dataset, "A"), path);
            var error = Should.Throw<EvaluationInputException>(() =>
                ReviewWorkflow.Import(dataset, Submitted(dataset, "A"), path));

            error.Message.ShouldContain("Refusing to overwrite");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_records_a_submission_without_touching_the_corpus()
    {
        var dataset = Dataset();
        var path = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}.json");
        try
        {
            ReviewWorkflow.Import(dataset, Submitted(dataset, "A"), path);

            var imported = EvaluationJson.Load<ReviewPackage>(path);
            imported.Status.ShouldBe("submitted");
            imported.ReviewerIdentity.ShouldBe("reviewer-a");

            // The corpus is untouched: importing a review never marks ground truth as reviewed.
            dataset.Documents[0].Clauses.ShouldAllBe(clause => clause.Status == "draft");
            dataset.Status.ShouldBe("draft");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Adjudication_preserves_both_answers_and_decides_nothing()
    {
        var dataset = Dataset();
        var a = Submitted(dataset, "A");
        var b = Submitted(dataset, "B") with
        {
            ReviewerIdentity = "reviewer-b",
            Clauses =
            [
                new ReviewClause("c1", "doc-1", "labour", null, null, "Cláusula de precio.", 1, 1, ["pricing"], "pricing", false, null),
                new ReviewClause("c2", "doc-1", "labour", null, null, "Cláusula de alcance.", 1, 1, ["pricing"], "pricing", false, null),
            ],
        };

        var worksheet = ReviewWorkflow.Adjudicate(dataset, a, b);

        worksheet.Rows.Count.ShouldBe(2);
        var row = worksheet.Rows.Single(entry => entry.ClauseId == "c1");
        row.ReviewerACategories.ShouldBe(["scope"]);
        row.ReviewerBCategories.ShouldBe(["pricing"]);
        row.Agreement.ShouldBe("disagree");
        row.DisagreementType.ShouldBe("categories+primary");
        row.Resolution.ShouldBeNull();
        row.Rationale.ShouldBeNull();
        row.Adjudicator.ShouldBeNull();
        row.FinalCategories.ShouldBeNull();

        var agreed = worksheet.Rows.Single(entry => entry.ClauseId == "c2");
        agreed.Agreement.ShouldBe("agree");
        agreed.DisagreementType.ShouldBeNull();
    }

    [Fact]
    public void Adjudication_rejects_an_incomplete_submission()
    {
        var dataset = Dataset();
        var a = Submitted(dataset, "A");
        var b = Submitted(dataset, "B", clauseFilter: clause => clause.ClauseId == "c1");

        Should.Throw<EvaluationInputException>(() => ReviewWorkflow.Adjudicate(dataset, a, b));
    }

    private static EvaluationDataset Dataset() =>
        new(
            "1",
            "unit-review",
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

    private static ReviewPackage Submitted(
        EvaluationDataset dataset,
        string slot,
        Func<ReviewClause, bool>? clauseFilter = null)
    {
        var package = ReviewWorkflow.ForReviewer(dataset, slot);
        var answers = package.Clauses
            .Where(clauseFilter ?? (_ => true))
            .Select(clause => clause with
            {
                Categories = clause.ClauseId == "c1" ? ["scope"] : ["pricing"],
                Primary = clause.ClauseId == "c1" ? "scope" : "pricing",
            })
            .ToList();

        return package with
        {
            ReviewerIdentity = slot == "A" ? "reviewer-a" : "reviewer-b",
            Status = "submitted",
            Clauses = answers,
        };
    }
}
