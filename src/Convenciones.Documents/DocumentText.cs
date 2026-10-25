using System.Text;
using Convenciones.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Convenciones.Documents;

/// <summary>Extracted text, one row per document. Mirrors <c>platform.document_text</c>.</summary>
public sealed class DocumentText
{
    private DocumentText()
    {
    }

    public Guid DocumentId { get; private set; }

    public string Content { get; private set; } = string.Empty;

    /// <summary>Constrained by the database to equal <c>length(content)</c>.</summary>
    public int CharCount { get; private set; }

    public DateTimeOffset ExtractedAt { get; private set; }

    public static DocumentText For(Guid documentId, string content, DateTimeOffset extractedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        return new DocumentText
        {
            DocumentId = documentId,
            Content = content,

            // PostgreSQL's length(text) counts code points, while string.Length counts UTF-16 code
            // units. Counting runes keeps char_count exactly equal to length(content) for text outside
            // the BMP, so the database CHECK cannot reject a genuinely successful extraction.
            CharCount = content.EnumerateRunes().Count(),
            ExtractedAt = extractedAt,
        };
    }
}

public sealed class DocumentTextConfiguration : IEntityTypeConfiguration<DocumentText>
{
    public void Configure(EntityTypeBuilder<DocumentText> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("document_text", "platform");
        builder.HasKey(text => text.DocumentId);

        builder.Property(text => text.DocumentId).HasColumnName("document_id");
        builder.Property(text => text.Content).HasColumnName("content").IsRequired();
        builder.Property(text => text.CharCount).HasColumnName("char_count");
        builder.Property(text => text.ExtractedAt).HasColumnName("extracted_at");
    }
}
