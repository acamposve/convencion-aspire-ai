using System.Text.Json;
using Convenciones.Worker.Extraction;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Extraction;

public sealed class ClauseResponseParserTests
{
    private static ClauseChunk Chunk() => new(1, 1, 1, "source", 0);

    [Fact]
    public void Missing_text_is_skipped_by_the_parser()
    {
        using var document = JsonDocument.Parse("""{"clauses":[{"page_from":1,"page_to":1,"confidence":0.9}]}""");

        ClauseResponseParser.Parse(document.RootElement, Chunk()).ShouldBeEmpty();
    }

    [Fact]
    public void Out_of_range_pages_are_clamped_to_the_chunk()
    {
        using var document = JsonDocument.Parse(
            """{"clauses":[{"text":"clause","page_from":9,"page_to":12,"confidence":0.9}]}""");

        var parsed = ClauseResponseParser.Parse(document.RootElement, Chunk());

        parsed.Single().PageFrom.ShouldBe(1);
        parsed.Single().PageTo.ShouldBe(1);
    }

    [Fact]
    public void Out_of_range_confidence_is_rejected()
    {
        using var document = JsonDocument.Parse(
            """{"clauses":[{"text":"clause","page_from":1,"page_to":1,"confidence":1.1}]}""");

        Should.Throw<ClauseExtractionException>(() => ClauseResponseParser.Parse(document.RootElement, Chunk()));
    }

    [Fact]
    public void Extra_fields_are_ignored_without_altering_required_fields()
    {
        using var document = JsonDocument.Parse(
            """{"clauses":[{"text":"clause","page_from":1,"page_to":1,"confidence":0.9,"id":"x","title":"y"}]}""");

        var parsed = ClauseResponseParser.Parse(document.RootElement, Chunk());

        parsed.Single().Text.ShouldBe("clause");
        parsed.Single().Number.ShouldBeNull();
        parsed.Single().Heading.ShouldBeNull();
    }
}
