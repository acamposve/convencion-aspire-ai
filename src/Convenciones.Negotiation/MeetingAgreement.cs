namespace Convenciones.Negotiation;

/// <summary>
/// An agreement reached in a meeting about a petition. Part of the case aggregate.
/// </summary>
/// <remarks>
/// Distinct from <c>app.agreement</c> (a collective agreement). The two composite foreign keys in the
/// schema force the petition and the meeting to belong to the same case as the agreement, which no
/// legacy constraint did (DS-28). In the MVP it is the negotiation record; promotion does not copy these
/// rows into agreement clauses (T11 D5).
/// </remarks>
public sealed class MeetingAgreement
{
    private MeetingAgreement()
    {
    }

    public Guid Id { get; private set; }

    public Guid CaseId { get; private set; }

    public Guid PetitionId { get; private set; }

    public Guid MeetingId { get; private set; }

    public DateOnly AgreedOn { get; private set; }

    public int Number { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public Guid? HeadingId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used as the optimistic concurrency token.</summary>
    public uint RowVersion { get; private set; }

    internal static MeetingAgreement Create(
        Guid id,
        Guid caseId,
        Guid petitionId,
        Guid meetingId,
        DateOnly agreedOn,
        int number,
        string? title,
        string? body,
        Guid? headingId,
        Guid actorId,
        DateTimeOffset now)
    {
        if (meetingId == Guid.Empty || petitionId == Guid.Empty)
        {
            throw NegotiationRuleException.Validation(
                "A meeting agreement must name both its petition and its meeting.");
        }

        if (agreedOn == default)
        {
            throw NegotiationRuleException.Validation("The agreement date is required.");
        }

        if (number < 1)
        {
            throw NegotiationRuleException.Validation("The agreement number must be a positive integer.");
        }

        if (headingId == Guid.Empty)
        {
            throw NegotiationRuleException.Validation(
                "The comparison heading must be a real heading id, or omitted when unclassified.");
        }

        return new MeetingAgreement
        {
            Id = id,
            CaseId = caseId,
            PetitionId = petitionId,
            MeetingId = meetingId,
            AgreedOn = agreedOn,
            Number = number,
            Title = NegotiationText.Required(title, "The agreement title is required."),
            Body = NegotiationText.Required(body, "The agreement body is required."),
            HeadingId = headingId,
            CreatedAt = now,
            CreatedBy = actorId,
            UpdatedAt = now,
            UpdatedBy = actorId,
        };
    }
}
