namespace Convenciones.Negotiation;

/// <summary>Shared text rules for the negotiation children.</summary>
internal static class NegotiationText
{
    public static string Required(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw NegotiationRuleException.Validation(message);
        }

        return value.Trim();
    }

    public static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Parsing for the string-valued enums in request bodies.</summary>
/// <remarks>
/// Explicit rather than <c>Enum.TryParse</c> alone, because that accepts a numeric string and would
/// silently map "1" to a state nobody asked for. The names are the database's labels.
/// </remarks>
public static class NegotiationEnumParsing
{
    public static bool TryParsePartyRole(string? value, out PartyRole role)
    {
        role = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "employer" => Set(PartyRole.Employer, out role),
            "union" => Set(PartyRole.Union, out role),
            "labourministry" => Set(PartyRole.LabourMinistry, out role),
            "other" => Set(PartyRole.Other, out role),
            _ => false,
        };
    }

    public static bool TryParsePetitionState(string? value, out PetitionState state)
    {
        state = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "open" => Set(PetitionState.Open, out state),
            "agreed" => Set(PetitionState.Agreed, out state),
            "rejected" => Set(PetitionState.Rejected, out state),
            "withdrawn" => Set(PetitionState.Withdrawn, out state),
            _ => false,
        };
    }

    public static bool TryParseCaseState(string? value, out CaseState state)
    {
        state = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "open" => Set(CaseState.Open, out state),
            "inprogress" => Set(CaseState.InProgress, out state),
            "pending" => Set(CaseState.Pending, out state),
            "agreed" => Set(CaseState.Agreed, out state),
            "closed" => Set(CaseState.Closed, out state),
            _ => false,
        };
    }

    private static bool Set<T>(T value, out T target)
        where T : struct
    {
        target = value;
        return true;
    }
}
