using Convenciones.Evaluation;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Evaluation;

/// <summary>
/// Page verification must be driven by the document, not by the draft annotation. These cases cover the
/// shapes that broke the first attempt: words joined across a line break, spans across a page boundary.
/// </summary>
public sealed class PdfPageLocatorTests
{
    [Fact]
    public void Finds_a_clause_that_lives_on_the_second_page()
    {
        var pages = new[]
        {
            "documento sintético cláusula primera objeto",
            "cláusula segunda precio y pago el precio total asciende a cien",
        };

        var span = PdfPageLocator.Find("El precio total asciende a cien", pages);

        span.ShouldBe((2, 2));
    }

    [Fact]
    public void Finds_a_clause_that_spans_two_pages()
    {
        var pages = new[]
        {
            "texto inicial que ocupa la primera página y continúa en la",
            "segunda página con el resto de la cláusula",
        };

        var span = PdfPageLocator.Find("que ocupa la primera página y continúa en la segunda página", pages);

        span.ShouldBe((1, 2));
    }

    [Fact]
    public void Ignores_accents_case_and_whitespace()
    {
        var pages = new[] { "CLAUSULA PRIMERA   Objeto del   convenio colectivo" };

        var span = PdfPageLocator.Find("cláusula primera objeto del convenio", pages);

        span.ShouldBe((1, 1));
    }

    [Fact]
    public void Returns_null_when_the_clause_is_absent()
    {
        var pages = new[] { "un documento distinto" };

        PdfPageLocator.Find("cláusula que no existe", pages).ShouldBeNull();
    }

    [Fact]
    public void Normalization_collapses_whitespace_and_strips_accents()
    {
        PdfPageLocator.Normalize("  Cláusula   PRIMERA,\n\n  objeto  ")
            .ShouldBe("clausula primera, objeto");
    }
}
