using Convenciones.Worker.Extraction;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Extraction;

/// <summary>
/// Pins each prompt version to the exact text it names. If a prompt changes, these tests fail until the
/// author bumps the version and re-pins the digest — which is the only thing that makes "this evaluation
/// used extraction/v2" an enforceable statement rather than a comment.
/// </summary>
public sealed class PromptCatalogTests
{
    // Re-pin only together with a version bump in PromptCatalog.
    private const string PinnedExtractionHash =
        "24c8b8a5c9566f68ebfa32d1803d07c34e799e429c1f930c4489e70db24fce4a";

    private const string PinnedClassificationHash =
        "29e3d65fc9cab8b7b0bd45a9fbe79e627867deb7f9ae3e929f19ab92dee4c9d3";

    [Fact]
    public void Extraction_version_is_pinned()
    {
        PromptCatalog.ExtractionVersion.ShouldBe("extraction/v2");
    }

    [Fact]
    public void Classification_version_is_pinned()
    {
        PromptCatalog.ClassificationVersion.ShouldBe("classification/v2");
    }

    [Fact]
    public void Extraction_prompt_text_matches_its_recorded_hash()
    {
        PromptCatalog.ExtractionContentHash.ShouldBe(
            PinnedExtractionHash,
            "The extraction system prompt changed. Bump PromptCatalog.ExtractionVersion and re-pin this " +
            "hash, otherwise historical evaluations will be read against the wrong contract.");
    }

    [Fact]
    public void Classification_prompt_text_matches_its_recorded_hash()
    {
        PromptCatalog.ClassificationContentHash.ShouldBe(
            PinnedClassificationHash,
            "The classification system prompt changed. Bump PromptCatalog.ClassificationVersion and re-pin " +
            "this hash, otherwise historical evaluations will be read against the wrong contract.");
    }

    [Fact]
    public void Versions_are_independent()
    {
        PromptCatalog.ExtractionVersion.ShouldNotBe(PromptCatalog.ClassificationVersion);
        PromptCatalog.ExtractionContentHash.ShouldNotBe(PromptCatalog.ClassificationContentHash);
    }

    [Fact]
    public void Options_default_to_the_catalog_versions()
    {
        new ExtractionOptions().PromptVersion.ShouldBe(PromptCatalog.ExtractionVersion);
        new ClassificationOptions().PromptVersion.ShouldBe(PromptCatalog.ClassificationVersion);
    }

    [Fact]
    public void Hash_is_a_lowercase_sha256_digest()
    {
        PromptCatalog.ExtractionContentHash.Length.ShouldBe(64);
        PromptCatalog.ExtractionContentHash.ShouldAllBe(character =>
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
    }

    [Fact]
    public void Hashing_is_sensitive_to_a_single_character()
    {
        PromptCatalog.Hash("contrato").ShouldNotBe(PromptCatalog.Hash("contratos"));
    }

    [Fact]
    public void Hashing_is_stable_across_line_endings()
    {
        // The working tree may be checked out with CRLF on Windows and LF on CI; the pin must not
        // depend on that, or it fails for a reason unrelated to the prompt changing.
        PromptCatalog.Hash("linea uno\nlinea dos").ShouldBe(PromptCatalog.Hash("linea uno\r\nlinea dos"));
    }
}
