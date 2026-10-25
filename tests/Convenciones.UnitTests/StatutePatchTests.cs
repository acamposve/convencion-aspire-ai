using System;
using System.Text.Json;
using Convenciones.Legal;
using Shouldly;
using Xunit;

using Convenciones.Catalog;

namespace Convenciones.UnitTests;

/// <summary>
/// The partial-update rule of the legal library: an omitted field is unchanged, an explicit null
/// clears a nullable field, and a required field sent as null stays null so the aggregate can refuse
/// it.
/// </summary>
/// <remarks>
/// The distinction has to survive JSON, so the serialisation tests are as important as the merge
/// tests: without them a regression that turned "omitted" into "null" would look like a working
/// API deleting a publication date.
/// </remarks>
public sealed class StatutePatchTests
{
    private static readonly Guid Actor = new("a1000000-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid Heading = new("a2000000-0000-0000-0000-000000000001");

    /// <summary>The API's serialiser settings: camelCase and case-insensitive property matching.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void An_omitted_field_is_unset_and_an_explicit_null_is_set()
    {
        var request = JsonSerializer.Deserialize<UpdateStatuteRequest>("""{"source": null}""", Json);

        request.ShouldNotBeNull();
        request!.Source.IsSet.ShouldBeTrue();
        request.Source.Value.ShouldBeNull();
        request.Name.IsSet.ShouldBeFalse();
        request.PublishedOn.IsSet.ShouldBeFalse();
        request.DocumentId.IsSet.ShouldBeFalse();
    }

    [Fact]
    public void Unset_fields_are_omitted_from_the_request_body()
    {
        var json = JsonSerializer.Serialize(
            new UpdateStatuteRequest(PatchField<string?>.Set("Nuevo"), default, default, default),
            Json);

        json.ShouldContain("\"name\":\"Nuevo\"", Case.Sensitive);
        json.ShouldNotContain("publishedOn", Case.Sensitive);
        json.ShouldNotContain("source", Case.Sensitive);
        json.ShouldNotContain("documentId", Case.Sensitive);
    }

    [Fact]
    public void A_field_sent_as_null_is_written_as_null()
    {
        var json = JsonSerializer.Serialize(
            new UpdateStatuteRequest(default, default, PatchField<string?>.Set(null), default),
            Json);

        json.ShouldContain("\"source\":null", Case.Sensitive);
        json.ShouldNotContain("\"name\"", Case.Sensitive);
    }

    [Fact]
    public void Merging_keeps_omitted_values_and_applies_explicit_nulls()
    {
        var statute = Statute.Create(
            StatuteKind.OtherStatute,
            "Ley Original",
            new DateOnly(2014, 1, 30),
            "Gaceta Oficial 40.340",
            null,
            Actor,
            Now);

        // Name sent, everything else omitted: the date and the source survive.
        var renamed = StatutePatch.Merge(
            statute,
            new UpdateStatuteRequest(PatchField<string?>.Set("Ley Renombrada"), default, default, default));

        renamed.Name.ShouldBe("Ley Renombrada");
        renamed.PublishedOn.ShouldBe(new DateOnly(2014, 1, 30));
        renamed.Source.ShouldBe("Gaceta Oficial 40.340");

        // Explicit nulls clear the nullable values and leave the name alone.
        var cleared = StatutePatch.Merge(
            statute,
            new UpdateStatuteRequest(
                default,
                PatchField<DateOnly?>.Set(null),
                PatchField<string?>.Set(null),
                default));

        cleared.Name.ShouldBe("Ley Original");
        cleared.PublishedOn.ShouldBeNull();
        cleared.Source.ShouldBeNull();
    }

    [Fact]
    public void Merging_an_article_keeps_omitted_values_and_preserves_a_null_required_field()
    {
        var statute = Statute.Create(
            StatuteKind.OtherStatute, "Ley de Artículos", null, null, null, Actor, Now);

        var article = statute.AddArticle(
            5, "Artículo 5", "Resumen", "Cuerpo", Heading, "Nota económica", Actor, Now);

        var retitled = StatutePatch.Merge(
            article,
            new UpdateStatuteArticleRequest(
                default,
                PatchField<string?>.Set("Artículo 5 — Nuevo"),
                default,
                default,
                default,
                default));

        retitled.Number.ShouldBe(5);
        retitled.Title.ShouldBe("Artículo 5 — Nuevo");
        retitled.Body.ShouldBe("Cuerpo");
        retitled.Summary.ShouldBe("Resumen");
        retitled.HeadingId.ShouldBe(Heading);
        retitled.EconomicComparisonNote.ShouldBe("Nota económica");

        // A required field sent as null survives the merge as null, which is what the command turns
        // into a 400 instead of a silent clear.
        var nulled = StatutePatch.Merge(
            article,
            new UpdateStatuteArticleRequest(
                PatchField<int?>.Set(null),
                PatchField<string?>.Set(null),
                PatchField<string?>.Set(null),
                default,
                PatchField<Guid?>.Set(null),
                default));

        nulled.Number.ShouldBeNull();
        nulled.Title.ShouldBeNull();
        nulled.Summary.ShouldBeNull();
        nulled.HeadingId.ShouldBeNull();
        nulled.Body.ShouldBe("Cuerpo");
    }
}
