using System.Text;
using UglyToad.PdfPig;

namespace Convenciones.Documents;

/// <summary>Outcome of reading a stored PDF. Absence of text is a failure, not an empty success.</summary>
public sealed record TextExtractionResult(bool Succeeded, string? Text, string? FailureReason)
{
    public static TextExtractionResult Success(string text) => new(true, text, null);

    public static TextExtractionResult Failure(string reason) => new(false, null, reason);
}

/// <summary>
/// Reads the text of a stored PDF.
/// </summary>
/// <remarks>
/// The same parser that validated the upload, so validation and extraction cannot disagree about what a
/// readable document is. A document that parses but yields no characters is reported as a
/// <em>failure</em>: the legacy system's characteristic defect was reporting success for an operation
/// that produced nothing.
/// </remarks>
public static class PdfTextExtractor
{
    public static TextExtractionResult Extract(Stream pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        try
        {
            pdf.Position = 0;

            using var document = PdfDocument.Open(pdf);
            var builder = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine(text);
                }
            }

            var content = builder.ToString().Trim();

            return content.Length == 0
                ? TextExtractionResult.Failure(
                    "The PDF was read but contains no extractable text. It may be a scanned image.")
                : TextExtractionResult.Success(content);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return TextExtractionResult.Failure(
                "The PDF could not be read for extraction. It may be damaged or password protected.");
        }
    }
}
