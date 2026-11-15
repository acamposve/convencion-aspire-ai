using Convenciones.Evaluation;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Evaluation;

public sealed class F06EvaluationTests
{
    [Fact]
    public void Perfect_extraction_and_classification_score_one()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1",
                Clause("c1", "Cláusula primera. Objeto del contrato.", "Primera", "Objeto", 1, 1, ["scope"], "scope"),
                Clause("c2", "Cláusula segunda. Precio y pago.", "Segunda", "Precio", 1, 1, ["pricing"], "pricing"))),
            Predictions(new PredictionDocument("doc-1",
            [
                Pred("Cláusula primera. Objeto del contrato.", "Primera", "Objeto", 1, 1, ["scope"], "scope"),
                Pred("Cláusula segunda. Precio y pago.", "Segunda", "Precio", 1, 1, ["pricing"], "pricing"),
            ])));

        result.Extraction.Matched.ShouldBe(2);
        result.Extraction.Precision.ShouldBe(1);
        result.Extraction.Recall.ShouldBe(1);
        result.Extraction.F1.ShouldBe(1);
        result.Fields.NumberRate.ShouldBe(1);
        result.Fields.HeadingRate.ShouldBe(1);
        result.Fields.PageRate.ShouldBe(1);
        result.Fields.TextRate.ShouldBe(1);
        result.Classification.MacroF1.ShouldBe(1);
        result.Classification.MicroF1.ShouldBe(1);
        result.Classification.ExactMatchRate.ShouldBe(1);
        result.Classification.PrimaryAccuracy.ShouldBe(1);
        result.Classification.UnclassifiedRate.ShouldBe(0);
    }

    [Fact]
    public void Missing_clause_is_counted_and_reduces_recall()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1",
                Clause("c1", "Cláusula primera. Objeto del contrato."),
                Clause("c2", "Cláusula segunda. Precio y pago del salario."))),
            Predictions(new PredictionDocument("doc-1", [Pred("Cláusula primera. Objeto del contrato.")])));

        result.Extraction.Matched.ShouldBe(1);
        result.Extraction.Annotated.ShouldBe(2);
        result.Extraction.Recall.ShouldBe(0.5);
        result.Documents[0].Missing.ShouldBe(1);
    }

    [Fact]
    public void Extra_clause_is_counted_and_reduces_precision()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula primera. Objeto del contrato."))),
            Predictions(new PredictionDocument("doc-1",
            [
                Pred("Cláusula primera. Objeto del contrato."),
                Pred("Texto inventado que no existe en el documento."),
            ])));

        result.Extraction.Matched.ShouldBe(1);
        result.Extraction.Predicted.ShouldBe(2);
        result.Extraction.Precision.ShouldBe(0.5);
        result.Documents[0].Extra.ShouldBe(1);
    }

    [Fact]
    public void Clause_below_the_overlap_threshold_is_not_matched()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula primera. Objeto del contrato y su alcance."))),
            Predictions(new PredictionDocument("doc-1", [Pred("Texto distinto sobre otra materia.")])));

        result.Extraction.Matched.ShouldBe(0);
        result.Extraction.F1.ShouldBe(0);
    }

    [Fact]
    public void Incorrect_heading_and_page_range_are_detected_on_a_matched_clause()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula primera. Objeto del contrato.", "Primera", "Objeto", 1, 2))),
            Predictions(new PredictionDocument("doc-1",
                [Pred("Cláusula primera. Objeto del contrato.", "Primera", "Alcance", 1, 1)])));

        result.Extraction.Matched.ShouldBe(1);
        result.Fields.NumberCorrect.ShouldBe(1);
        result.Fields.HeadingCorrect.ShouldBe(0);
        result.Fields.HeadingRate.ShouldBe(0);
        result.Fields.PageCorrect.ShouldBe(0);
        result.Fields.PageRate.ShouldBe(0);
    }

    [Fact]
    public void Wrong_categories_produce_a_false_positive_and_a_false_negative()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula primera. Precio y pago.", categories: ["pricing"], primary: "pricing"))),
            Predictions(new PredictionDocument("doc-1", [Pred("Cláusula primera. Precio y pago.", categories: ["scope"], primary: "scope")])));

        result.Classification.MacroF1.ShouldBe(0);
        result.Classification.MicroF1.ShouldBe(0);
        result.Classification.FalsePositiveCategories.ShouldBe(1);
        result.Classification.PrimaryAccuracy.ShouldBe(0);

        var pricing = result.Classification.PerCategory.Single(category => category.Category == "pricing");
        pricing.FalseNegatives.ShouldBe(1);
        pricing.Support.ShouldBe(1);

        var scope = result.Classification.PerCategory.Single(category => category.Category == "scope");
        scope.FalsePositives.ShouldBe(1);
        scope.Support.ShouldBe(0);
    }

    [Fact]
    public void Multi_label_matches_are_scored_per_category()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula sobre partes y alcance.", categories: ["parties", "scope"], primary: "scope"))),
            Predictions(new PredictionDocument("doc-1",
                [Pred("Cláusula sobre partes y alcance.", categories: ["scope"], primary: "scope")])));

        result.Classification.ExactMatch.ShouldBe(0);
        result.Classification.ExactMatchRate.ShouldBe(0);
        // scope is a perfect single-label match (F1 = 1); parties is entirely missed (F1 = 0).
        // Macro-F1 averages the two supported categories: (1 + 0) / 2 = 0.5.
        result.Classification.MacroF1.ShouldBe(0.5);
        result.Classification.PrimaryAccuracy.ShouldBe(1);
    }

    [Fact]
    public void Unclassified_clause_is_measured()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula sin categoría aplicable.", categories: []))),
            Predictions(new PredictionDocument("doc-1", [Pred("Cláusula sin categoría aplicable.")])));

        result.Classification.EvaluatedClauses.ShouldBe(1);
        result.Classification.Unclassified.ShouldBe(1);
        result.Classification.UnclassifiedRate.ShouldBe(1);
        result.Classification.ExpectedEmpty.ShouldBe(1);
        result.Classification.ExactMatchRate.ShouldBe(1);
    }

    [Fact]
    public void Category_without_positive_examples_is_listed_but_excluded_from_macro()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula de precio.", categories: ["pricing"], primary: "pricing"))),
            Predictions(new PredictionDocument("doc-1",
                [Pred("Cláusula de precio.", categories: ["pricing", "ip"], primary: "pricing")])));

        result.Classification.MacroF1.ShouldBe(1);
        var ip = result.Classification.PerCategory.Single(category => category.Category == "ip");
        ip.Support.ShouldBe(0);
        ip.FalsePositives.ShouldBe(1);
        result.Classification.FalsePositiveCategories.ShouldBe(1);
    }

    [Fact]
    public void Empty_dataset_is_rejected()
    {
        var dataset = new EvaluationDataset("v1", "corpus", "extraction/v1", "taxonomy-v1", []);
        var predictions = new PredictionSet("run-1", "test", "v1", "extraction/v1", []);

        Should.Throw<EvaluationInputException>(() => EvaluationEngine.Evaluate(dataset, predictions));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Annotation_with_blank_text_is_rejected(string text)
    {
        var dataset = Dataset(Doc("doc-1", Clause("c1", text)));
        var predictions = Predictions(new PredictionDocument("doc-1", [Pred("algo")]));

        Should.Throw<EvaluationInputException>(() => EvaluationEngine.Evaluate(dataset, predictions));
    }

    [Fact]
    public void Duplicate_clause_ids_are_rejected()
    {
        var dataset = Dataset(Doc("doc-1",
            Clause("c1", "Primera cláusula."),
            Clause("c1", "Segunda cláusula.")));

        Should.Throw<EvaluationInputException>(() =>
            EvaluationEngine.Evaluate(dataset, Predictions(new PredictionDocument("doc-1", [Pred("Primera cláusula.")]))));
    }

    [Fact]
    public void Primary_outside_categories_is_rejected()
    {
        var dataset = Dataset(Doc("doc-1", Clause("c1", "Cláusula de precio.", categories: ["pricing"], primary: "scope")));

        Should.Throw<EvaluationInputException>(() =>
            EvaluationEngine.Evaluate(dataset, Predictions(new PredictionDocument("doc-1", [Pred("Cláusula de precio.")]))));
    }

    [Fact]
    public void Invalid_page_range_is_rejected()
    {
        var dataset = Dataset(Doc("doc-1", Clause("c1", "Cláusula de precio.", pageFrom: 2, pageTo: 1)));

        Should.Throw<EvaluationInputException>(() =>
            EvaluationEngine.Evaluate(dataset, Predictions(new PredictionDocument("doc-1", [Pred("Cláusula de precio.")]))));
    }

    [Fact]
    public void Predictions_for_a_different_dataset_version_are_rejected()
    {
        var dataset = Dataset(Doc("doc-1", Clause("c1", "Cláusula de precio.")));
        var predictions = new PredictionSet("run-1", "test", "v2", "extraction/v1",
            [new PredictionDocument("doc-1", [Pred("Cláusula de precio.")])]);

        var error = Should.Throw<EvaluationInputException>(() => EvaluationEngine.Evaluate(dataset, predictions));
        error.Message.ShouldContain("v2");
    }

    [Fact]
    public void Scoring_the_same_inputs_twice_is_deterministic()
    {
        var dataset = Dataset(Doc("doc-1",
            Clause("c1", "Cláusula primera. Objeto.", "Primera", "Objeto", 1, 1, ["scope"], "scope"),
            Clause("c2", "Cláusula segunda. Precio.", "Segunda", "Precio", 1, 1, ["pricing"], "pricing")));
        var predictions = Predictions(new PredictionDocument("doc-1",
        [
            Pred("Cláusula primera. Objeto.", "Primera", "Objeto", 1, 1, ["scope"], "scope"),
            Pred("Cláusula segunda. Precio.", "Segunda", "Precio", 1, 1, [], null),
        ]));

        var first = EvaluationEngine.Evaluate(dataset, predictions);
        var second = EvaluationEngine.Evaluate(dataset, predictions);

        second.Extraction.ShouldBe(first.Extraction);
        second.Fields.ShouldBe(first.Fields);
        second.Classification.MacroF1.ShouldBe(first.Classification.MacroF1);
        second.Classification.PerCategory.Select(category => category.Category)
            .ShouldBe(first.Classification.PerCategory.Select(category => category.Category));
    }

    private static EvaluationDataset Dataset(params CorpusDocument[] documents) =>
        new("v1", "unit-corpus", "extraction/v1", "taxonomy-20260109000100", documents);

    private static PredictionSet Predictions(params PredictionDocument[] documents) =>
        new("run-fixture", "fixture-model", "v1", "extraction/v1", documents);

    private static CorpusDocument Doc(string id, params AnnotatedClauseRecord[] clauses) => new(id, id, clauses);

    private static AnnotatedClauseRecord Clause(
        string id,
        string text,
        string? number = null,
        string? heading = null,
        int pageFrom = 1,
        int pageTo = 1,
        IReadOnlyList<string>? categories = null,
        string? primary = null) =>
        new(id, text, number, heading, pageFrom, pageTo, categories ?? [], primary);

    private static PredictedClauseRecord Pred(
        string text,
        string? number = null,
        string? heading = null,
        int pageFrom = 1,
        int pageTo = 1,
        IReadOnlyList<string>? categories = null,
        string? primary = null) =>
        new(number, heading, text, pageFrom, pageTo, categories ?? [], primary);
}
