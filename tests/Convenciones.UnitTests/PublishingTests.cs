using System;
using Convenciones.Agreements;
using Convenciones.Catalog;
using Convenciones.Publishing;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests;

/// <summary>
/// Rich-text sanitisation (A-33, AC-S10) and the news state machine.
/// </summary>
/// <remarks>
/// The sanitiser is the library's, so these tests assert the *configuration*: scripts and event handlers
/// are gone, formatting survives, images and styles are not part of the allow-list, and a body that was
/// only markup is refused rather than stored empty.
/// </remarks>
public sealed class PublishingTests
{
    private static readonly Guid Actor = new("a1000000-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Category = new("a2000000-0000-0000-0000-000000000001");

    [Fact]
    public void A_script_tag_is_removed_and_the_text_survives()
    {
        var cleaned = RichText.Clean("<p>Hola</p><script>alert('x')</script>");

        cleaned.ShouldNotBeNull();
        cleaned!.ShouldNotContain("script", Case.Insensitive);
        cleaned.ShouldContain("Hola");
    }

    [Fact]
    public void Event_handlers_and_javascript_urls_are_removed()
    {
        var cleaned = RichText.Clean(
            "<p onclick=\"steal()\">Texto</p><a href=\"javascript:alert(1)\">enlace</a>");

        cleaned.ShouldNotBeNull();
        cleaned!.ShouldNotContain("onclick", Case.Insensitive);
        cleaned.ShouldNotContain("javascript:", Case.Insensitive);
        cleaned.ShouldContain("Texto");
    }

    [Fact]
    public void Formatting_and_safe_links_are_kept_and_links_get_a_rel()
    {
        var cleaned = RichText.Clean("<p><b>Negrita</b> y <a href=\"https://example.test\">enlace</a></p>");

        cleaned.ShouldNotBeNull();
        cleaned!.ShouldContain("<b>");
        cleaned.ShouldContain("https://example.test");
        cleaned.ShouldContain("rel=");
        cleaned.ShouldContain("noopener");
    }

    [Fact]
    public void Images_and_styles_are_not_part_of_the_allow_list()
    {
        var cleaned = RichText.Clean(
            "<p style=\"color:red\">Texto</p><img src=\"https://tracker.test/pixel.png\" />");

        cleaned.ShouldNotBeNull();
        cleaned!.ShouldNotContain("img", Case.Insensitive);
        cleaned.ShouldNotContain("tracker.test");
        cleaned.ShouldNotContain("style=", Case.Insensitive);
        cleaned.ShouldContain("Texto");
    }

    [Fact]
    public void A_body_that_was_only_markup_becomes_null_so_the_required_check_refuses_it()
    {
        RichText.Clean("<script>alert(1)</script>").ShouldBeNull();
        RichText.Clean("   ").ShouldBeNull();
    }

    [Fact]
    public void A_new_article_is_a_draft_with_a_sanitised_body()
    {
        var article = NewsArticle.Create(
            Category, "Título", "Resumen", "<p>Cuerpo</p><script>x()</script>", Actor, Now);

        article.State.ShouldBe(PublicationState.Draft);
        article.PublishedAt.ShouldBeNull();
        article.PublishedBy.ShouldBeNull();
        article.Body.ShouldNotContain("script", Case.Insensitive);
        article.Body.ShouldContain("Cuerpo");
    }

    [Fact]
    public void Publishing_records_the_actor_and_the_time_and_twice_is_a_conflict()
    {
        var article = NewsArticle.Create(Category, "Título", null, "Cuerpo", Actor, Now);

        article.Publish(Actor, Now);

        article.State.ShouldBe(PublicationState.Published);
        article.PublishedAt.ShouldBe(Now);
        article.PublishedBy.ShouldBe(Actor);

        Should.Throw<NewsRuleException>(() => article.Publish(Actor, Now))
            .Code.ShouldBe("already_published");
    }

    [Fact]
    public void Unpublishing_clears_the_publication_columns_and_unpublishing_a_draft_is_a_conflict()
    {
        var article = NewsArticle.Create(Category, "Título", null, "Cuerpo", Actor, Now);

        Should.Throw<NewsRuleException>(() => article.Unpublish(Actor, Now))
            .Code.ShouldBe("not_published");

        article.Publish(Actor, Now);
        article.Unpublish(Actor, Now);

        article.State.ShouldBe(PublicationState.Draft);
        article.PublishedAt.ShouldBeNull();
        article.PublishedBy.ShouldBeNull();
    }

    [Fact]
    public void The_title_and_the_body_are_required_and_a_blank_category_is_refused()
    {
        Should.Throw<NewsRuleException>(() =>
            NewsArticle.Create(Category, "  ", null, "Cuerpo", Actor, Now));

        Should.Throw<NewsRuleException>(() =>
            NewsArticle.Create(Category, "Título", null, "   ", Actor, Now));

        Should.Throw<NewsRuleException>(() =>
            NewsArticle.Create(Guid.Empty, "Título", null, "Cuerpo", Actor, Now));
    }

    [Fact]
    public void A_category_name_is_required_and_the_sort_order_cannot_be_negative()
    {
        Should.Throw<NewsRuleException>(() => NewsCategory.Create("  ", null, 0, Now));

        Should.Throw<NewsRuleException>(() => NewsCategory.Create("Noticias", null, -1, Now));

        NewsCategory.Create("Noticias", null, 3, Now).SortOrder.ShouldBe(3);
    }
}
