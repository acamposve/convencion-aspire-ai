using UglyToad.PdfPig;

namespace Convenciones.Evaluation;

public sealed record DocumentProfile(
    string FileName,
    int Pages,
    int Characters,
    int NonWhitespaceCharacters,
    double CharactersPerPage,
    bool RequiresOcr,
    TimeSpan Duration);

/// <summary>
/// Local PDF profile: pages, text density and whether the document looks scanned.
/// No model call and no upload; it is the first, cheap gate of the evaluation.
/// </summary>
public static class DocumentProfiler
{
    public static DocumentProfile Profile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var started = DateTime.UtcNow;
        using var document = PdfDocument.Open(path);
        var pages = 0;
        var characters = 0;
        var nonWhitespace = 0;

        foreach (var page in document.GetPages())
        {
            pages++;
            var text = page.Text ?? string.Empty;
            characters += text.Length;
            nonWhitespace += text.Count(character => !char.IsWhiteSpace(character));
        }

        var perPage = pages == 0 ? 0 : (double)nonWhitespace / pages;
        return new DocumentProfile(
            Path.GetFileName(path),
            pages,
            characters,
            nonWhitespace,
            Math.Round(perPage, 1),
            perPage < 50,
            DateTime.UtcNow - started);
    }
}
