using Convenciones.Evaluation;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Evaluation;

/// <summary>
/// Adversarial cases for the matching algorithm and for metric edges the core suite does not cover.
/// These document behaviour rather than assert a preferred algorithm: changing the matcher must change
/// these expectations deliberately, and would change every historical score.
/// </summary>
public sealed class F06HardeningTests
{
    [Fact]
    public void Duplicate_annotations_are_matched_one_to_one()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1",
                Clause("c1", "Cláusula idéntica repetida en el documento."),
                Clause("c2", "Cláusula idéntica repetida en el documento."))),
            Predictions(new PredictionDocument("doc-1",
            [
                Pred("Cláusula idéntica repetida en el documento."),
                Pred("Cláusula idéntica repetida en el documento."),
            ])));

        result.Extraction.Matched.ShouldBe(2);
        result.Extraction.Precision.ShouldBe(1);
        result.Extraction.Recall.ShouldBe(1);
    }

    [Fact]
    public void One_prediction_cannot_satisfy_two_annotations()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1",
                Clause("c1", "Cláusula repetida."),
                Clause("c2", "Cláusula repetida."))),
            Predictions(new PredictionDocument("doc-1", [Pred("Cláusula repetida.")])));

        result.Extraction.Matched.ShouldBe(1);
        result.Extraction.Recall.ShouldBe(0.5);
        result.Documents[0].Missing.ShouldBe(1);
    }

    [Fact]
    public void Reordered_clauses_still_match()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1",
                Clause("c1", "Cláusula primera sobre el objeto."),
                Clause("c2", "Cláusula segunda sobre el precio."))),
            Predictions(new PredictionDocument("doc-1",
            [
                Pred("Cláusula segunda sobre el precio."),
                Pred("Cláusula primera sobre el objeto."),
            ])));

        result.Extraction.Matched.ShouldBe(2);
        result.Extraction.Precision.ShouldBe(1);
        result.Extraction.Recall.ShouldBe(1);
    }

    [Fact]
    public void Overlap_exactly_at_the_threshold_matches_and_just_below_does_not()
    {
        // Ten characters; longest common substring of eight is exactly 0.8.
        var atThreshold = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "ABCDEFGHIJ"))),
            Predictions(new PredictionDocument("doc-1", [Pred("ABCDEFGHXY")])));

        atThreshold.Extraction.Matched.ShouldBe(1);

        var belowThreshold = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "ABCDEFGHIJ"))),
            Predictions(new PredictionDocument("doc-1", [Pred("ABCDEFGXYZ")])));

        belowThreshold.Extraction.Matched.ShouldBe(0);
    }

    [Fact]
    public void An_over_broad_prediction_absorbs_the_first_clause_and_leaves_the_rest_missing()
    {
        // Documented limitation: the ratio denominator is the annotation length, so a prediction that
        // contains the clause matches with 1.0 regardless of how much extra text it carries.
        var whole = "Primera cláusula sobre el objeto. Segunda cláusula sobre el precio.";
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1",
                Clause("c1", "Primera cláusula sobre el objeto."),
                Clause("c2", "Segunda cláusula sobre el precio."))),
            Predictions(new PredictionDocument("doc-1", [Pred(whole)])));

        result.Extraction.Matched.ShouldBe(1);
        result.Extraction.Precision.ShouldBe(1);
        result.Extraction.Recall.ShouldBe(0.5);
    }

    [Fact]
    public void Extra_prediction_cannot_inflate_field_level_accuracy()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula primera. Objeto.", "Primera", "Objeto", 1, 1))),
            Predictions(new PredictionDocument("doc-1",
            [
                Pred("Cláusula primera. Objeto.", "DISTINTA", "Distinto", 9, 9),
                Pred("Texto adicional que no corresponde.", "Otra", "Otro", 5, 5),
            ])));

        // Only the matched pair is scored for fields; the extra clause is an extraction error, not a
        // field error.
        result.Extraction.Matched.ShouldBe(1);
        result.Fields.Matched.ShouldBe(1);
        result.Fields.NumberCorrect.ShouldBe(0);
        result.Fields.HeadingCorrect.ShouldBe(0);
        result.Fields.PageCorrect.ShouldBe(0);
        result.Extraction.Precision.ShouldBe(0.5);
    }

    [Fact]
    public void Missing_predictions_leave_classification_at_zero_rather_than_dividing_by_zero()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula sobre el precio.", categories: ["pricing"], primary: "pricing"))),
            Predictions());

        result.Extraction.Matched.ShouldBe(0);
        result.Classification.EvaluatedClauses.ShouldBe(0);
        result.Classification.MacroF1.ShouldBe(0);
        result.Classification.MicroF1.ShouldBe(0);
        result.Classification.ExactMatchRate.ShouldBe(0);
        result.Classification.UnclassifiedRate.ShouldBe(0);
        result.Classification.PrimaryAccuracy.ShouldBe(0);
    }

    [Fact]
    public void Predicted_category_outside_the_reference_taxonomy_is_counted_as_a_false_positive()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1", Clause("c1", "Cláusula de precio.", categories: ["pricing"], primary: "pricing"))),
            Predictions(new PredictionDocument("doc-1",
                [Pred("Cláusula de precio.", categories: ["pricing", "categoria_inventada"], primary: "pricing")])));

        var invented = result.Classification.PerCategory.Single(entry => entry.Category == "categoria_inventada");
        invented.Support.ShouldBe(0);
        invented.FalsePositives.ShouldBe(1);
        result.Classification.FalsePositiveCategories.ShouldBe(1);
        // Macro only averages supported categories, so the invented one cannot depress it.
        result.Classification.MacroF1.ShouldBe(1);
    }

    [Fact]
    public void Unclassified_rate_uses_evaluated_clauses_as_its_denominator()
    {
        var result = EvaluationEngine.Evaluate(
            Dataset(Doc("doc-1",
                Clause("c1", "Cláusula de precio.", categories: ["pricing"], primary: "pricing"),
                Clause("c2", "Cláusula sin categoría.", categories: []))),
            Predictions(new PredictionDocument("doc-1",
            [
                Pred("Cláusula de precio.", categories: ["pricing"], primary: "pricing"),
                Pred("Cláusula sin categoría."),
            ])));

        result.Classification.EvaluatedClauses.ShouldBe(2);
        result.Classification.Unclassified.ShouldBe(1);
        result.Classification.UnclassifiedRate.ShouldBe(0.5);
        result.Classification.ExpectedEmpty.ShouldBe(1);
    }

    private static EvaluationDataset Dataset(params CorpusDocument[] documents) =>
        new("v1", "hardening-corpus", "extraction/v1", "taxonomy-20260109000100", documents);

    private static PredictionSet Predictions(params PredictionDocument[] documents) =>
        new("run-hardening", "fixture-model", "v1", "extraction/v1", documents);

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
