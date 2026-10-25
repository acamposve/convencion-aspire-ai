namespace Convenciones.Agreements;

/// <summary>
/// The composed agreement title (Q-1). Never stored; shared by the agreement screens and the
/// comparison read model so the two cannot disagree.
/// </summary>
public static class AgreementTitles
{
    public static string Compose(string companyName, DateOnly validFrom, DateOnly? validTo)
    {
        var name = string.IsNullOrWhiteSpace(companyName) ? "Empresa" : companyName.Trim();

        if (validTo is not { } end)
        {
            return $"{name} · desde {validFrom.Year}";
        }

        return validFrom.Year == end.Year
            ? $"{name} · {validFrom.Year}"
            : $"{name} · {validFrom.Year}–{end.Year}";
    }
}
