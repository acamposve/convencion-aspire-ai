using System.Text;
using Convenciones.Ai.DocumentIntelligence;

namespace Convenciones.Worker.Extraction;

/// <summary>Page-aware chunking with overlap, tracking the page range of every window.</summary>
public static class ClauseChunker
{
    public static IReadOnlyList<ClauseChunk> Chunk(DocumentLayout layout, ExtractionOptions options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ChunkCharacters <= options.OverlapCharacters || options.OverlapCharacters < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ChunkCharacters must exceed OverlapCharacters.");
        }

        var builder = new StringBuilder();
        var pages = new List<(int Number, int Start, int End)>();

        foreach (var page in layout.Pages)
        {
            var start = builder.Length;
            builder.Append(page.NormalizedText);
            builder.Append('\n');
            pages.Add((page.PageNumber, start, builder.Length));
        }

        var content = builder.ToString();
        if (content.Length == 0 || pages.Count == 0)
        {
            return [];
        }

        if (content.Length <= options.ChunkCharacters)
        {
            return [new ClauseChunk(1, pages[0].Number, pages[^1].Number, content, 0)];
        }

        var chunks = new List<ClauseChunk>();
        var offset = 0;
        var index = 1;

        while (offset < content.Length && chunks.Count <= options.MaxChunks)
        {
            var length = Math.Min(options.ChunkCharacters, content.Length - offset);
            var text = content.Substring(offset, length);
            var end = offset + length;
            var pageFrom = pages.First(page => page.End > offset && page.Start < end).Number;
            var pageTo = pages.Last(page => page.End > offset && page.Start < end).Number;
            chunks.Add(new ClauseChunk(index, pageFrom, pageTo, text, offset));

            if (end >= content.Length)
            {
                break;
            }

            offset = end - options.OverlapCharacters;
            index++;
        }

        return chunks;
    }
}
