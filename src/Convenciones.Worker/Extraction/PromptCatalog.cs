using System.Security.Cryptography;
using System.Text;

namespace Convenciones.Worker.Extraction;

/// <summary>
/// The declared version of each prompt contract plus a SHA-256 of its exact text.
/// </summary>
/// <remarks>
/// The version is what operators, logs and evaluation reports read. The hash is what proves the
/// version was not reused after the text changed: <c>PromptCatalogTests</c> pins both, so editing a
/// prompt fails a test until the author bumps the version and re-pins the digest.
///
/// Only the system prompt is hashed. It carries the output contract and the safety rules; the user
/// prompt is a data envelope whose content varies with every document, so hashing it would version
/// the data rather than the contract.
/// </remarks>
public static class PromptCatalog
{
    /// <summary>PR #95 replaced the contract; v1 must never be read as this text.</summary>
    public const string ExtractionVersion = "extraction/v2";

    /// <summary>PR #95 replaced the contract; v1 must never be read as this text.</summary>
    public const string ClassificationVersion = "classification/v2";

    public static string ExtractionContentHash { get; } = Hash(ExtractionPrompt.System);

    public static string ClassificationContentHash { get; } = Hash(ClassificationPrompt.System);

    internal static string Hash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Normalise line endings before hashing: the same prompt text must hash identically whether the
        // working tree was checked out with LF (CI) or CRLF (Windows). Without this, the pin fails for
        // reasons unrelated to the prompt having changed.
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
