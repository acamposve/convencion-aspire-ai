using System;
using System.Linq;
using Convenciones.Legal;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

/// <summary>
/// The legal library's invariants, exercised on the aggregate without a server or a database.
/// </summary>
/// <remarks>
/// The database asserts the same rules a second time — <c>statute_name_not_blank</c>,
/// <c>statute_article_number_positive</c>, <c>statute_article_title_not_blank</c>,
/// <c>statute_article_body_not_blank</c>, the unique article number and the partial unique index that
/// allows one labour statute. Neither layer is the only guard (constitution Art. IV.3).
/// </remarks>
public sealed class StatuteAggregateTests
{
    private static readonly Guid Actor = new("a1000000-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_statute_requires_a_name()
    {
        foreach (var name in new[] { null, "", "   " })
        {
            var exception = Should.Throw<StatuteRuleException>(() => Create(name: name));

            exception.Kind.ShouldBe(StatuteRuleKind.Validation);
            exception.Code.ShouldBe("validation_failed");
            exception.Message.ShouldContain("name");
        }
    }

    [Fact]
    public void A_statute_keeps_its_kind_and_its_name_is_trimmed()
    {
        var statute = Create(name: "  Código del Trabajo  ", kind: StatuteKind.LabourLaw);

        statute.Name.ShouldBe("Código del Trabajo");
        statute.Kind.ShouldBe(StatuteKind.LabourLaw);
        statute.IsLabourLaw.ShouldBeTrue();
        statute.CreatedBy.ShouldBe(Actor);
        statute.ArticleCount.ShouldBe(0);
    }

    [Fact]
    public void An_undefined_kind_is_refused()
    {
        var exception = Should.Throw<StatuteRuleException>(() => Create(kind: (StatuteKind)99));

        exception.Kind.ShouldBe(StatuteRuleKind.Validation);
    }

    [Fact]
    public void The_kind_cannot_be_changed_by_an_update()
    {
        var statute = Create(kind: StatuteKind.OtherStatute);

        statute.UpdateHeader("Ley Orgánica del Trabajo", null, null, null, Actor, Now);

        statute.Kind.ShouldBe(StatuteKind.OtherStatute);
        statute.Name.ShouldBe("Ley Orgánica del Trabajo");
    }

    [Fact]
    public void Blank_optional_fields_become_null()
    {
        var statute = Create(name: "  Ley  ", source: "   ");

        statute.Source.ShouldBeNull();
    }

    [Fact]
    public void An_article_number_must_be_a_positive_integer()
    {
        var statute = Create();

        foreach (var number in new[] { 0, -1 })
        {
            var exception = Should.Throw<StatuteRuleException>(() => AddArticle(statute, number));

            exception.Kind.ShouldBe(StatuteRuleKind.Validation);
            exception.Message.ShouldContain("positive integer");
        }
    }

    [Fact]
    public void An_article_without_its_heading_or_body_is_refused()
    {
        var statute = Create();

        Should.Throw<StatuteRuleException>(() => AddArticle(statute, title: "  "))
            .Message.ShouldContain("heading");

        Should.Throw<StatuteRuleException>(() => AddArticle(statute, title: null))
            .Kind.ShouldBe(StatuteRuleKind.Validation);

        Should.Throw<StatuteRuleException>(() => AddArticle(statute, body: "   "))
            .Message.ShouldContain("body");

        statute.ArticleCount.ShouldBe(0);
    }

    [Fact]
    public void An_article_may_be_unclassified_but_never_carry_an_empty_heading_id()
    {
        var statute = Create();

        var unclassified = AddArticle(statute, headingId: null);
        unclassified.HeadingId.ShouldBeNull();

        var classification = Guid.NewGuid();
        var classified = AddArticle(statute, number: 2, headingId: classification);
        classified.HeadingId.ShouldBe(classification);

        // An empty Guid is the client failing to send null, not a classification.
        Should.Throw<StatuteRuleException>(() => AddArticle(statute, number: 3, headingId: Guid.Empty))
            .Kind.ShouldBe(StatuteRuleKind.Validation);
    }

    [Fact]
    public void Article_numbers_are_unique_within_a_statute_but_not_across_statutes()
    {
        var first = Create(name: "Ley A");
        var second = Create(name: "Ley B");

        AddArticle(first, 1);

        var duplicate = Should.Throw<StatuteRuleException>(() => AddArticle(first, 1));
        duplicate.Kind.ShouldBe(StatuteRuleKind.Conflict);
        duplicate.Code.ShouldBe("duplicate_article_number");

        // The same number in a different statute is a different article entirely.
        Should.NotThrow(() => AddArticle(second, 1));
        first.ArticleCount.ShouldBe(1);
        second.ArticleCount.ShouldBe(1);
    }

    [Fact]
    public void Renumbering_checks_against_the_other_articles()
    {
        var statute = Create();
        AddArticle(statute, 1);
        var second = AddArticle(statute, 2);

        Should.Throw<StatuteRuleException>(() => statute.EnsureNumberAvailable(1, second.Id))
            .Kind.ShouldBe(StatuteRuleKind.Conflict);

        // Keeping its own number is not a conflict.
        Should.NotThrow(() => statute.EnsureNumberAvailable(2, second.Id));
    }

    [Fact]
    public void Removing_an_article_updates_the_count()
    {
        var statute = Create();
        var article = AddArticle(statute, 1);

        statute.FindArticle(article.Id).ShouldBeSameAs(article);
        statute.RemoveArticle(article);

        statute.ArticleCount.ShouldBe(0);
        statute.FindArticle(article.Id).ShouldBeNull();
    }

    [Fact]
    public void Articles_are_projected_in_numeric_order()
    {
        var statute = Create();
        AddArticle(statute, 10);
        AddArticle(statute, 2);
        AddArticle(statute, 1);

        var detail = StatuteResponses.Detail(statute);

        detail.Articles.Select(article => article.Number).ShouldBe(new[] { 1, 2, 10 });
    }

    [Fact]
    public void The_kind_parser_accepts_only_the_two_names()
    {
        StatuteKinds.TryParse("LabourLaw", out var labour).ShouldBeTrue();
        labour.ShouldBe(StatuteKind.LabourLaw);

        StatuteKinds.TryParse("  otherstatute ", out var other).ShouldBeTrue();
        other.ShouldBe(StatuteKind.OtherStatute);

        // A numeric string would otherwise bind through Enum.TryParse and silently pick a kind.
        StatuteKinds.TryParse("1", out _).ShouldBeFalse();
        StatuteKinds.TryParse("Promoted", out _).ShouldBeFalse();
        StatuteKinds.TryParse(null, out _).ShouldBeFalse();
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static Statute Create(
        string? name = "Ley del Trabajo",
        StatuteKind kind = StatuteKind.OtherStatute,
        string? source = null) =>
        Statute.Create(kind, name, null, source, null, Actor, Now);

    private static StatuteArticle AddArticle(
        Statute statute,
        int number = 1,
        string? title = "Artículo 1 — Objeto",
        string? body = "Texto del artículo.",
        Guid? headingId = null) =>
        statute.AddArticle(number, title, null, body, headingId, null, Actor, Now);
}
