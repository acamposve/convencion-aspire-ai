using Convenciones.Evaluation;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Evaluation;

/// <summary>
/// The spreadsheet is only a front-end: it must produce exactly the JSON <c>review-import</c> accepts,
/// and it must fail loudly on anything a reviewer could get wrong.
/// </summary>
public sealed class ReviewExcelTests
{
    private static readonly string[] Taxonomy = ["scope", "pricing", "liability", "other"];

    [Fact]
    public void Valid_spreadsheet_round_trips_into_a_submission()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            ReviewExcelWorkbook.Write(path, dataset, Filled(dataset, "reviewer-a"));

            var submission = ReviewExcelWorkbook.Read(path, dataset, "A");

            submission.ReviewerIdentity.ShouldBe("reviewer-a");
            submission.Status.ShouldBe("submitted");
            submission.Clauses.Count.ShouldBe(2);
            submission.Clauses.Single(clause => clause.ClauseId == "c1").Categories.ShouldBe(["scope"]);
            submission.Clauses.Single(clause => clause.ClauseId == "c1").Primary.ShouldBe("scope");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Multiple_selected_categories_are_preserved()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            var package = Filled(dataset, "reviewer-a") with
            {
                Clauses =
                [
                    new ReviewClause("c1", "doc-1", "labour", null, null, "Cláusula de precio.", 1, 1, ["scope", "pricing"], "scope", false, null),
                    new ReviewClause("c2", "doc-1", "labour", null, null, "Cláusula de alcance.", 1, 1, ["scope"], "scope", false, null),
                ],
            };
            ReviewExcelWorkbook.Write(path, dataset, package);

            var submission = ReviewExcelWorkbook.Read(path, dataset, "A");

            submission.Clauses.Single(clause => clause.ClauseId == "c1").Categories.ShouldBe(["scope", "pricing"]);
            submission.Clauses.Single(clause => clause.ClauseId == "c1").Primary.ShouldBe("scope");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reviewer_notes_and_ambiguity_are_preserved()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            var package = Filled(dataset, "reviewer-a") with
            {
                Clauses =
                [
                    new ReviewClause("c1", "doc-1", "labour", null, null, "texto", 1, 1, ["scope"], "scope", true, "Podría ser 'other'; ver cláusula 4."),
                    new ReviewClause("c2", "doc-1", "labour", null, null, "texto", 1, 1, ["pricing"], "pricing", false, null),
                ],
            };
            ReviewExcelWorkbook.Write(path, dataset, package);

            var submission = ReviewExcelWorkbook.Read(path, dataset, "A");

            var clause = submission.Clauses.Single(entry => entry.ClauseId == "c1");
            clause.Ambiguous.ShouldBeTrue();
            clause.Notes.ShouldBe("Podría ser 'other'; ver cláusula 4.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unknown_category_identifies_the_row_and_field()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            ReviewExcelWorkbook.Write(path, dataset, Filled(dataset, "reviewer-a", c1Categories: ["working_conditions"]));

            var error = Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "A"));

            error.Errors.ShouldContain(entry =>
                entry.Row == 8 && entry.Field == "Categorías" && entry.Message.Contains("working_conditions"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Primary_outside_the_selection_is_rejected()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            ReviewExcelWorkbook.Write(path, dataset, Filled(dataset, "reviewer-a", c1Primary: "liability"));

            var error = Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "A"));

            error.Errors.ShouldContain(entry =>
                entry.Field == "Categoría principal" && entry.Message.Contains("no está entre"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_reviewer_identity_is_rejected()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            ReviewExcelWorkbook.Write(path, dataset, Filled(dataset, reviewer: null));

            var error = Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "A"));

            error.Errors.ShouldContain(entry => entry.Row == 3 && entry.Field == "Reviewer identity");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_clause_is_rejected()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            var package = Filled(dataset, "reviewer-a") with
            {
                Clauses = [Filled(dataset, "reviewer-a").Clauses[0]],
            };
            ReviewExcelWorkbook.Write(path, dataset, package);

            var error = Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "A"));

            error.Errors.ShouldContain(entry => entry.Message.Contains("Falta la cláusula 'c2'"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Duplicate_clause_ids_are_rejected()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            var first = Filled(dataset, "reviewer-a").Clauses[0];
            var package = Filled(dataset, "reviewer-a") with { Clauses = [first, first] };
            ReviewExcelWorkbook.Write(path, dataset, package);

            var error = Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "A"));

            error.Errors.ShouldContain(entry => entry.Message.Contains("Cláusula duplicada"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unknown_clause_id_is_rejected()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            var package = Filled(dataset, "reviewer-a") with
            {
                Clauses =
                [
                    .. Filled(dataset, "reviewer-a").Clauses,
                    new ReviewClause("invented", "doc-1", "labour", null, null, "texto", 1, 1, ["scope"], "scope", false, null),
                ],
            };
            ReviewExcelWorkbook.Write(path, dataset, package);

            var error = Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "A"));

            error.Errors.ShouldContain(entry => entry.Message.Contains("Cláusula desconocida"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Invalid_ambiguity_value_is_rejected_not_ignored()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            ReviewExcelWorkbook.Write(path, dataset, Filled(dataset, "reviewer-a"));
            SetCellText(path, "J8", "Quizás");

            var error = Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "A"));

            error.Errors.ShouldContain(entry =>
                entry.Row == 8 && entry.Field == "¿Ambigua?" && entry.Message.Contains("Sí o No"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Named_but_untouched_sheet_is_rejected_as_empty()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            var package = ReviewWorkflow.ForReviewer(dataset, "A") with { ReviewerIdentity = "reviewer-a" };
            ReviewExcelWorkbook.Write(path, dataset, package);

            var error = Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "A"));

            error.Errors.ShouldContain(entry => entry.Field == "Planilla vacía");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Excel_file_is_never_overwritten_silently()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            ReviewExcelWorkbook.Write(path, dataset, Filled(dataset, "reviewer-a"));

            var error = Should.Throw<EvaluationInputException>(() =>
                ReviewExcelWorkbook.Write(path, dataset, Filled(dataset, "reviewer-a")));

            error.Message.ShouldContain("No se sobrescribe");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Converted_submission_passes_the_authoritative_import_validation()
    {
        var dataset = Dataset();
        var path = TempFile();
        var imported = TempFile();
        try
        {
            ReviewExcelWorkbook.Write(path, dataset, Filled(dataset, "reviewer-a"));
            var submission = ReviewExcelWorkbook.Read(path, dataset, "A");

            Should.NotThrow(() => ReviewWorkflow.Import(dataset, submission, imported));

            var stored = EvaluationJson.Load<ReviewPackage>(imported);
            stored.ReviewerIdentity.ShouldBe("reviewer-a");
            stored.Status.ShouldBe("submitted");
        }
        finally
        {
            File.Delete(path);
            if (File.Exists(imported))
            {
                File.Delete(imported);
            }
        }
    }

    [Fact]
    public void Template_has_no_answers_and_keeps_every_clause()
    {
        var dataset = Dataset();
        var path = TempFile();
        try
        {
            ReviewExcelWorkbook.Write(path, dataset, ReviewWorkflow.ForReviewer(dataset, "B"));

            // A blank template cannot be read as a submission: identity and answers are missing.
            Should.Throw<ReviewExcelException>(() => ReviewExcelWorkbook.Read(path, dataset, "B"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static EvaluationDataset Dataset() =>
        new(
            "1",
            "unit-excel",
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

    private static ReviewPackage Filled(
        EvaluationDataset dataset,
        string? reviewer,
        string[]? c1Categories = null,
        string? c1Primary = "scope")
    {
        var package = ReviewWorkflow.ForReviewer(dataset, "A");
        var categories = c1Categories ?? ["scope"];

        return package with
        {
            ReviewerIdentity = reviewer,
            Clauses =
            [
                new ReviewClause("c1", "doc-1", "labour", null, null, "Cláusula de precio.", 1, 1, categories, c1Primary, false, null),
                new ReviewClause("c2", "doc-1", "labour", null, null, "Cláusula de alcance.", 1, 1, ["scope"], "scope", false, null),
            ],
        };
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}.xlsx");

    private static void SetCellText(string path, string reference, string value)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbookPart = document.WorkbookPart!;
        var sheet = workbookPart.Workbook!.Sheets!.Elements<Sheet>().First(entry => entry.Name == ReviewExcelWorkbook.AnnotationSheet);
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
        var worksheet = worksheetPart.Worksheet!;
        var cell = worksheet.Descendants<Cell>().First(entry => entry.CellReference == reference);
        cell.DataType = CellValues.InlineString;
        cell.InlineString = new InlineString(new Text(value));
        worksheet.Save();
    }
}
