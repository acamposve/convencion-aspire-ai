namespace Convenciones.Negotiation;

/// <summary>
/// An employer offer, always in response to exactly one petition (A-20). Part of the case aggregate.
/// </summary>
public sealed class EmployerOffer
{
    private EmployerOffer()
    {
    }

    public Guid Id { get; private set; }

    public Guid PetitionId { get; private set; }

    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    internal static EmployerOffer Create(
        Guid id,
        Guid petitionId,
        int number,
        string? title,
        string? body,
        Guid actorId,
        DateTimeOffset now)
    {
        if (number < 1)
        {
            throw NegotiationRuleException.Validation("The offer number must be a positive integer.");
        }

        return new EmployerOffer
        {
            Id = id,
            PetitionId = petitionId,
            Number = number,
            Title = NegotiationText.Required(title, "The offer title is required."),
            Body = NegotiationText.Required(body, "The offer body is required."),
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };
    }
}
