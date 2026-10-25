namespace Convenciones.Negotiation;

/// <summary>
/// A negotiation meeting. Part of the case aggregate.
/// </summary>
/// <remarks>
/// <c>Attendees</c> stays free text, as the schema documents: structured attendance would be a new
/// requirement, and the parties are modelled separately.
/// </remarks>
public sealed class NegotiationMeeting
{
    private NegotiationMeeting()
    {
    }

    public Guid Id { get; private set; }

    public Guid CaseId { get; private set; }

    public DateOnly HeldOn { get; private set; }

    public TimeOnly? StartTime { get; private set; }

    public string? Duration { get; private set; }

    public string? Summary { get; private set; }

    public string? Details { get; private set; }

    public string? Attendees { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    internal static NegotiationMeeting Create(
        Guid id,
        Guid caseId,
        DateOnly heldOn,
        TimeOnly? startTime,
        string? duration,
        string? summary,
        string? details,
        string? attendees,
        Guid actorId,
        DateTimeOffset now)
    {
        if (heldOn == default)
        {
            throw NegotiationRuleException.Validation("The meeting date is required.");
        }

        return new NegotiationMeeting
        {
            Id = id,
            CaseId = caseId,
            HeldOn = heldOn,
            StartTime = startTime,
            Duration = NegotiationText.Optional(duration),
            Summary = NegotiationText.Optional(summary),
            Details = NegotiationText.Optional(details),
            Attendees = NegotiationText.Optional(attendees),
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };
    }
}
