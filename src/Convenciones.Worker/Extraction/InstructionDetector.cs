namespace Convenciones.Worker.Extraction;

/// <summary>
/// Cheap signal that a clause looks like an instruction rather than legal text. It never changes the
/// schema or calls a tool; it only forces the clause into human review.
/// </summary>
public static class InstructionDetector
{
    private static readonly string[] Patterns =
    [
        "ignora las instrucciones",
        "ignore previous",
        "ignore all previous",
        "system prompt",
        "prompt del sistema",
        "actúa como",
        "act as",
        "jailbreak",
        "devuelve solo",
        "return only",
        "no reveles",
        "do not reveal",
    ];

    public static bool IsInstructionLike(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Patterns.Any(pattern => text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
}
