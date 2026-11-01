using Convenciones.Evaluation;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Evaluation;

public sealed class EvaluationMetricsTests
{
    [Fact]
    public void Exact_clauses_match_with_full_f1()
    {
        var annotated = new[] { new AnnotatedClause("Cláusula primera. Objeto.", 1, 1) };
        var predicted = new[] { new PredictedClause("Cláusula primera. Objeto.", 1, 1) };

        var result = BoundaryMetrics.Compute(annotated, predicted);

        result.Matched.ShouldBe(1);
        result.Precision.ShouldBe(1);
        result.Recall.ShouldBe(1);
        result.F1.ShouldBe(1);
    }

    [Fact]
    public void Missing_clause_reduces_recall()
    {
        var annotated = new[]
        {
            new AnnotatedClause("Cláusula primera. Objeto.", 1, 1),
            new AnnotatedClause("Cláusula segunda. Jornada.", 2, 2),
        };
        var predicted = new[] { new PredictedClause("Cláusula primera. Objeto.", 1, 1) };

        var result = BoundaryMetrics.Compute(annotated, predicted);

        result.Precision.ShouldBe(1);
        result.Recall.ShouldBe(0.5);
        result.F1.ShouldBeLessThan(0.7);
    }

    [Fact]
    public void Partial_text_below_threshold_does_not_match()
    {
        var annotated = new[] { new AnnotatedClause("Cláusula primera. Objeto del contrato.", 1, 1) };
        var predicted = new[] { new PredictedClause("Otra cosa completamente distinta.", 1, 1) };

        var result = BoundaryMetrics.Compute(annotated, predicted);

        result.Matched.ShouldBe(0);
    }

    [Fact]
    public void Macro_f1_averages_categories_with_support()
    {
        var annotated = new[]
        {
            new ClassificationAnnotation(1, new HashSet<string> { "pricing" }),
            new ClassificationAnnotation(2, new HashSet<string> { "termination" }),
        };
        var predicted = new[]
        {
            new HashSet<string> { "pricing" },
            new HashSet<string>(),
        };

        var result = ClassificationMetrics.Compute(annotated, predicted);

        result.PerCategory["pricing"].ShouldBe(1);
        result.PerCategory["termination"].ShouldBe(0);
        result.MacroF1.ShouldBe(0.5);
    }
}
