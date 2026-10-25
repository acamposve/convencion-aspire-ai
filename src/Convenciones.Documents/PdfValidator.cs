using UglyToad.PdfPig;

namespace Convenciones.Documents;

/// <summary>
/// Decides whether a byte stream is a PDF, by parsing it.
/// </summary>
/// <remarks>
/// <para>
/// The declared <c>Content-Type</c>, the file extension and the filename are never consulted. They are
/// attacker-controlled strings, and the legacy system trusted exactly that field
/// (<c>$_FILES['type']</c>) while writing files into a web-served directory.
/// </para>
/// <para>
/// A <c>%PDF-</c> prefix is also not accepted as evidence. It is a four-byte claim that anything can
/// make; the file must parse into a document with at least one page. Bytes that are recognisably a
/// different format are reported as <see cref="PdfValidationOutcome.WrongFormat"/> (415) rather than as
/// a broken PDF (400), because telling a user to "check their PDF" when they sent a JPEG is unhelpful.
/// </para>
/// </remarks>
public static class PdfValidator
{
    private const int MaxHeaderScan = 1024;

    private static readonly byte[] PdfHeader = "%PDF-"u8.ToArray();
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] ZipHeader = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] OleHeader = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static PdfValidationResult Validate(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            return PdfValidationResult.Malformed("The file could not be read for validation.");
        }

        stream.Position = 0;

        if (stream.Length == 0)
        {
            return PdfValidationResult.Malformed("The file is empty.");
        }

        var head = new byte[Math.Min(MaxHeaderScan, stream.Length)];
        stream.Position = 0;
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;

        var span = head.AsSpan(0, read);

        if (!span.StartsWith(JpegHeader)
            && !span.StartsWith(PngHeader)
            && !span.StartsWith(ZipHeader)
            && !span.StartsWith(OleHeader)
            && !LooksLikePlainText(span)
            && !ContainsPdfHeader(span))
        {
            return PdfValidationResult.WrongFormat(
                "The file is not a PDF. Only PDF documents are accepted.");
        }

        if (span.StartsWith(JpegHeader) || span.StartsWith(PngHeader) || span.StartsWith(ZipHeader)
            || span.StartsWith(OleHeader))
        {
            return PdfValidationResult.WrongFormat(
                "The file is not a PDF. Only PDF documents are accepted, regardless of its name or "
                + "declared content type.");
        }

        if (LooksLikePlainText(span) && !ContainsPdfHeader(span))
        {
            return PdfValidationResult.WrongFormat(
                "The file is not a PDF. Only PDF documents are accepted.");
        }

        if (!ContainsPdfHeader(span))
        {
            return PdfValidationResult.WrongFormat(
                "The file is not a PDF. Only PDF documents are accepted.");
        }

        return ParsePdf(stream);
    }

    /// <summary>
    /// The real test: the bytes must become a document with a page.
    /// </summary>
    private static PdfValidationResult ParsePdf(Stream stream)
    {
        try
        {
            stream.Position = 0;

            using var document = PdfDocument.Open(stream);

            if (document.NumberOfPages < 1)
            {
                return PdfValidationResult.Malformed("The PDF contains no pages.");
            }

            // Touching the first page forces the page tree to be resolved: a file can declare pages
            // and still have none that can be read, and that is not a usable document.
            _ = document.GetPage(1).Size;

            return PdfValidationResult.Valid();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A password-protected PDF is a valid file that yields nothing: it is rejected here, so
            // the user is told now rather than discovering it as an extraction failure later.
            return PdfValidationResult.Malformed(
                "The PDF could not be read. It may be damaged, truncated, or password protected.");
        }
    }

    /// <summary>
    /// The specification tolerates leading bytes before <c>%PDF-</c>, so the header is searched within
    /// the first 1 KiB rather than required at offset zero.
    /// </summary>
    private static bool ContainsPdfHeader(ReadOnlySpan<byte> span) => span.IndexOf(PdfHeader) >= 0;

    private static bool LooksLikePlainText(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0)
        {
            return false;
        }

        foreach (var value in span)
        {
            if (value is >= 0x20 and <= 0x7E or 0x09 or 0x0A or 0x0D)
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
