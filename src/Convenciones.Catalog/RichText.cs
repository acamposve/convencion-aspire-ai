using Ganss.Xss;

namespace Convenciones.Catalog;

/// <summary>
/// Sanitisation for every rich-text column in the product (A-33).
/// </summary>
/// <remarks>
/// <para>
/// Rich text is sanitised **on write**, with a maintained allow-list library, and the sanitised value is
/// what is stored. The legacy system stored rich text and re-emitted it raw (§S8); a hand-rolled filter
/// protecting legal documents is the worse risk, so the library does the parsing and the allow-list is
/// configuration rather than code.
/// </para>
/// <para>
/// One sanitiser for clause, annex and news bodies: a new rich-text field cannot accidentally skip the
/// step, because <see cref="Clean"/> is the only way those aggregates accept that text. Callers can
/// still echo the stored value as HTML, so what is stored must already be safe.
/// </para>
/// </remarks>
public static class RichText
{
    private static readonly HtmlSanitizer Sanitizer = Create();

    /// <summary>
    /// Sanitises the markup. Blank input, and input that was nothing but markup the allow-list removes,
    /// both become <see langword="null"/>, so the aggregate's existing "required" check refuses it
    /// instead of storing an empty string.
    /// </summary>
    public static string? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var cleaned = Sanitizer.Sanitize(html);

        return string.IsNullOrWhiteSpace(System.Net.WebUtility.HtmlDecode(cleaned)) ? null : cleaned;
    }

    public static string Sanitize(string html) => Sanitizer.Sanitize(html);

    private static HtmlSanitizer Create()
    {
        var sanitizer = new HtmlSanitizer();

        // Formatting only, as A-33 says. Images are not part of a clause or a news body: a public page
        // that loads remote images leaks the reader's address to whoever hosts them, and the product's
        // attachments are documents, not inline pictures.
        sanitizer.AllowedTags.Remove("img");

        // No styling: the client's design system decides how a clause looks.
        sanitizer.AllowedAttributes.Remove("style");
        sanitizer.AllowedCssProperties.Clear();

        // Links stay, but only navigable schemes, and they cannot reach back into the opener.
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");

        sanitizer.PostProcessNode += (_, args) =>
        {
            if (args.Node is not AngleSharp.Dom.IElement element
                || element.LocalName != "a"
                || string.IsNullOrEmpty(element.GetAttribute("href")))
            {
                return;
            }

            element.SetAttribute("rel", "nofollow noopener noreferrer");
        };

        return sanitizer;
    }
}
