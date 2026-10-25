using System.Text.RegularExpressions;

namespace Convenciones.Negotiation;

/// <summary>
/// A representative party of a negotiation. Rows, not the legacy twelve fixed columns (DS-27).
/// </summary>
/// <remarks>
/// Personal data (name, phone, email). Deliberately not <see cref="Catalog.IAuditable"/>: an audit
/// <c>changes</c> payload would copy the values, and the security model forbids personal data in audit
/// rows beyond field names (<c>security-model.md</c> §5.5).
/// </remarks>
public sealed class NegotiationParty
{
    private static readonly Regex EmailPattern = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private NegotiationParty()
    {
    }

    public Guid Id { get; private set; }

    public Guid CaseId { get; private set; }

    public PartyRole Role { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Position { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    internal static NegotiationParty Create(
        Guid id,
        Guid caseId,
        PartyRole role,
        string? name,
        string? phone,
        string? email,
        string? position,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(role))
        {
            throw NegotiationRuleException.Validation("The party role is not recognized.");
        }

        var cleanedEmail = NegotiationText.Optional(email);

        if (cleanedEmail is not null && !EmailPattern.IsMatch(cleanedEmail))
        {
            throw NegotiationRuleException.Validation("The party email is not a valid address.");
        }

        return new NegotiationParty
        {
            Id = id,
            CaseId = caseId,
            Role = role,
            Name = NegotiationText.Required(name, "The party name is required."),
            Phone = NegotiationText.Optional(phone),
            Email = cleanedEmail,
            Position = NegotiationText.Optional(position),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
