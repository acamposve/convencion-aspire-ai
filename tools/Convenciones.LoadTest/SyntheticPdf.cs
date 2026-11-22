using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Convenciones.LoadTest;

/// <summary>
/// Builds real, parseable PDF bytes for the upload scenario.
/// </summary>
/// <remarks>
/// The API validates the file with PdfPig, so a padded blob would be rejected with 415/400 and the
/// upload scenario would measure the error path. This is the same structure the integration tests
/// use; the cross-reference offsets are computed from the emitted bytes.
/// </remarks>
public static class SyntheticPdf
{
    /// <summary>A valid one-page PDF of exactly <paramref name="targetLength"/> bytes.</summary>
    public static byte[] OfSize(long targetLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetLength, 400);

        var unpadded = Build(0);
        var padding = (int)(targetLength - unpadded.Length);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Build(padding);
            if (candidate.Length == targetLength)
            {
                return candidate;
            }

            padding += (int)(targetLength - candidate.Length);
        }

        throw new InvalidOperationException($"Could not build a PDF of exactly {targetLength} bytes.");
    }

    private static byte[] Build(int paddingLength)
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
