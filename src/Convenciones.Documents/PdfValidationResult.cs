namespace Convenciones.Documents;

public enum PdfValidationOutcome
{
    /// <summary>A parseable PDF with at least one page.</summary>
    Valid,

    /// <summary>Recognisably another format: JPEG, PNG, ZIP, OLE, text, or unrecognised bytes.</summary>
    WrongFormat,

    /// <summary>The bytes claim to be a PDF but do not parse into one.</summary>
    Malformed,
}

/// <summary>
/// The result of inspecting the bytes. <see cref="Reason"/> is shown to the caller; the enum decides
/// the status code.
/// </summary>
/// <remarks>
/// The distinction matters: a JPEG is <see cref="WrongFormat"/> and earns a 415, while a truncated PDF
/// is <see cref="Malformed"/> and earns a 400. Returning 415 for a broken PDF would tell the user to
/// convert a file that is already the right format.
/// </remarks>
public sealed record PdfValidationResult(PdfValidationOutcome Outcome, string? Reason)
{
    public bool IsValid => Outcome is PdfValidationOutcome.Valid;

    public static PdfValidationResult Valid() => new(PdfValidationOutcome.Valid, null);

    public static PdfValidationResult WrongFormat(string reason) =>
        new(PdfValidationOutcome.WrongFormat, reason);

    public static PdfValidationResult Malformed(string reason) =>
        new(PdfValidationOutcome.Malformed, reason);
}
