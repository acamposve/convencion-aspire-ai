using Convenciones.Catalog;

namespace Convenciones.Agreements;

/// <summary>
/// Annex metadata attached to an agreement. Part of the <see cref="Agreement"/> aggregate.
/// </summary>
public sealed class AgreementAnnex : IAuditable
{
    private AgreementAnnex()
    {
    }

    public Guid Id { get; private set; }

    public Guid AgreementId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? Body { get; private set; }

    public Guid? DocumentId { get; private set; }

    public int SortOrder { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public uint RowVersion { get; private set; }

    public string AuditModule => "agreements";

    public string AuditEntityType => "agreement_annex";

    public string AuditEntityId => Id.ToString();

    public static AgreementAnnex Create(
        Guid id,
        Guid agreementId,
        string? name,
        string? description,
        string? body,
        Guid? documentId,
        int sortOrder,
        Guid actorId,
        DateTimeOffset now)
    {
        var annex = new AgreementAnnex
        {
            Id = id,
            AgreementId = agreementId,
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
        };

        annex.Apply(name, description, body, documentId, sortOrder, actorId, now);
        return annex;
    }

    public void Update(
        string? name,
        string? description,
        string? body,
        Guid? documentId,
        int sortOrder,
        Guid actorId,
        DateTimeOffset now) =>
        Apply(name, description, body, documentId, sortOrder, actorId, now);

    private void Apply(
        string? name,
        string? description,
        string? body,
        Guid? documentId,
        int sortOrder,
        Guid actorId,
        DateTimeOffset now)
    {
        Name = AgreementText.Required(name, "The annex name is required.");
        Description = AgreementText.Optional(description);
        Body = RichText.Clean(body);
        DocumentId = documentId;
        SortOrder = sortOrder;
        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}

/// <summary>Shared text rules for agreement children.</summary>
internal static class AgreementText
{
    public static string Required(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw AgreementRuleException.Validation(message);
        }

        return value.Trim();
    }

    public static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
