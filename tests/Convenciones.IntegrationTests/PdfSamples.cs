using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Builds real, parseable PDF bytes — and the files that only pretend to be PDFs.
/// </summary>
/// <remarks>
/// A test that cannot produce a valid PDF cannot prove that a valid PDF is accepted, and a test that
/// sends a truncated file without knowing it is truncated proves nothing. These builders are explicit
/// about the structure, including the cross-reference offsets, so the "valid" sample really parses.
/// </remarks>
internal static class PdfSamples
{
    /// <summary>A minimal but genuinely valid one-page PDF.</summary>
    public static byte[] ValidPdf() => BuildValid(0);

    /// <summary>
    /// A valid PDF of exactly <paramref name="targetLength"/> bytes, padded with a PDF comment.
    /// </summary>
    /// <remarks>
    /// The padding is a comment placed before the cross-reference table, which is legal and does not
    /// change any object offset — only the position of <c>xref</c>, which is recomputed. The size is
    /// iterated because changing the xref offset can change its digit count, which changes the total.
    /// </remarks>
    public static byte[] ValidPdfOfSize(long targetLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetLength, 200);

        var unpadded = BuildValid(0);
        var padding = (int)(targetLength - unpadded.Length);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = BuildValid(padding);

            if (candidate.Length == targetLength)
            {
                return candidate;
            }

            padding += (int)(targetLength - candidate.Length);
        }

        throw new InvalidOperationException(
            $"Could not build a PDF of exactly {targetLength} bytes.");
    }

    public static byte[] TruncatedPdf()
    {
        var valid = ValidPdf();

        // Cut in the middle of the body: the header is intact, the structure is not.
        return valid[..(valid.Length / 2)];
    }

    /// <summary>Starts with a PDF header and contains nothing a parser can use.</summary>
    public static byte[] HeaderOnlyPdf() =>
        Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not a pdf body, there is no xref and no page\n%%EOF\n");

    public static byte[] PlainText() =>
        Encoding.UTF8.GetBytes("Estimado cliente: este archivo no es un PDF.\n");

    public static byte[] Jpeg() =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, .. Enumerable.Repeat((byte)0x00, 64)];

    public static byte[] Png() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, .. Enumerable.Repeat((byte)0x00, 64)];

    /// <summary>A ZIP container: what a DOCX actually is.</summary>
    public static byte[] ZipContainer() =>
        [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00, .. Enumerable.Repeat((byte)0x00, 64)];

    /// <summary>An OLE compound file: what a legacy DOC actually is.</summary>
    public static byte[] OleDocument() =>
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. Enumerable.Repeat((byte)0x00, 64)];

    public static byte[] Empty() => [];

    private static byte[] BuildValid(int paddingLength)
    {
        var builder = new StringBuilder();
        var offsets = new List<int>();

        builder.Append("%PDF-1.7\n");

        // A binary comment marks the file as containing binary data, per the specification.
        builder.Append("%\u00E2\u00E3\u00CF\u00D3\n");

        var body = new[]
        {
            "1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n",
            "2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n",
            "3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<<>>>>\nendobj\n",
        };

        foreach (var item in body)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(item);
        }

        if (paddingLength > 0)
        {
            // A comment is ignored by any parser and does not disturb object offsets.
            builder.Append('%');
            builder.Append(new string('P', Math.Max(0, paddingLength - 2)));
            builder.Append('\n');
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());

        builder.Append("xref\n0 4\n");
        builder.Append("0000000000 65535 f \n");

        // Each entry is exactly 20 bytes, as the specification requires.
        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("D10", CultureInfo.InvariantCulture));
            builder.Append(" 00000 n \n");
        }

        builder.Append("trailer\n<</Size 4/Root 1 0 R>>\nstartxref\n");
        builder.Append(xrefOffset.ToString(CultureInfo.InvariantCulture));
        builder.Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
