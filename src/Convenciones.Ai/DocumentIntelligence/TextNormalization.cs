using System.Text;

namespace Convenciones.Ai.DocumentIntelligence;

/// <summary>Normalised text plus, for each normalised character, the index it came from.</summary>
public sealed record NormalizedText(string Text, IReadOnlyList<int> OriginalOffsets);

/// <summary>
/// Collapses whitespace without losing the link to the original text.
/// </summary>
/// <remarks>
/// Document Intelligence returns line breaks that are meaningful for layout but not for the model. The
/// pipeline chunks the normalised text, so it needs to map a normalised offset back to the page text for
/// source references.
/// </remarks>
public static class TextNormalization
{
    public static NormalizedText Normalize(string original)
    {
        ArgumentNullException.ThrowIfNull(original);

        var builder = new StringBuilder(original.Length);
        var offsets = new List<int>(original.Length);
        var pendingWhitespaceIndex = -1;

        for (var index = 0; index < original.Length; index++)
        {
            var character = original[index];
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && pendingWhitespaceIndex < 0)
                {
                    pendingWhitespaceIndex = index;
                }

                continue;
            }

            if (pendingWhitespaceIndex >= 0)
            {
                builder.Append(' ');
                offsets.Add(pendingWhitespaceIndex);
                pendingWhitespaceIndex = -1;
            }

            builder.Append(character);
            offsets.Add(index);
        }

        return new NormalizedText(builder.ToString(), offsets);
    }
}
