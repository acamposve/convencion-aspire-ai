using System.Text.Json;
using System.Text.Json.Nodes;
using Convenciones.Worker.Extraction;
using Json.Schema;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Extraction;

/// <summary>
/// The extraction request is sent in JSON object mode, so the provider never receives
/// <see cref="ExtractionSchema.Json"/>: the prompt is the only place the model can learn the contract.
/// These tests keep the prompt and the validator in agreement without asserting large blocks of prose.
/// </summary>
public sealed class ExtractionPromptContractTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(ExtractionSchema.Json);

    [Fact]
    public void Embedded_example_satisfies_the_extraction_schema()
    {
        Evaluate(JsonNode.Parse(ExtractionPrompt.ExampleJson)!).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void System_prompt_embeds_the_exact_validated_example()
    {
        ExtractionPrompt.System.ShouldContain(ExtractionPrompt.ExampleJson);
    }

    [Fact]
    public void System_prompt_names_every_property_the_schema_defines()
    {
        foreach (var name in SchemaPropertyNames())
        {
            ExtractionPrompt.System.ShouldContain($"\"{name}\"");
        }
    }

    [Fact]
    public void System_prompt_documents_the_contract_instead_of_pointing_to_an_unavailable_schema()
    {
        // The old instruction asked for "el JSON del esquema" while the schema was never sent.
        ExtractionPrompt.System.ShouldNotContain("esquema");
        ExtractionPrompt.System.ShouldContain("Contrato de campos");
    }

    [Fact]
    public void System_prompt_forbids_additional_properties()
    {
        ExtractionPrompt.System.ShouldContain("sin propiedades adicionales");
        ExtractionPrompt.System.ShouldContain("No añadas ninguna otra propiedad");
    }

    [Fact]
    public void Required_fields_are_documented_as_obligatory_and_optional_fields_as_nullable()
    {
        var (required, optional) = ItemFields();

        foreach (var name in required)
        {
            ContractLine(name).ShouldContain("obligatorio");
        }

        foreach (var name in optional)
        {
            ContractLine(name).ShouldContain("null");
        }
    }

    [Fact]
    public void Documented_constraints_match_the_schema()
    {
        ContractLine("clauses").ShouldContain("array");
        ContractLine("text").ShouldContain("no vacío");
        ContractLine("page_from").ShouldContain("mayor o igual que 1");
        ContractLine("page_to").ShouldContain("mayor o igual que 1");
        ContractLine("confidence").ShouldContain("entre 0 y 1");
    }

    [Fact]
    public void The_validator_still_rejects_an_extra_property()
    {
        var instance = JsonNode.Parse(ExtractionPrompt.ExampleJson)!.AsObject();
        instance["clauses"]!.AsArray()[0]!.AsObject()["alias"] = "page";

        Evaluate(instance).IsValid.ShouldBeFalse();
    }

    private static EvaluationResults Evaluate(JsonNode instance)
    {
        using var document = JsonDocument.Parse(instance.ToJsonString());
        return Schema.Evaluate(
            document.RootElement.Clone(),
            new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    private static string ContractLine(string property)
    {
        var prefix = $"- \"{property}\":";
        var line = ExtractionPrompt.System
            .Split('\n')
            .Select(candidate => candidate.Trim())
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));

        line.ShouldNotBeNull($"the prompt has no contract line for '{property}'");
        return line;
    }

    private static IEnumerable<string> SchemaPropertyNames()
    {
        using var document = JsonDocument.Parse(ExtractionSchema.Json);
        var names = new List<string>();
        var root = document.RootElement.GetProperty("properties");
        foreach (var property in root.EnumerateObject())
        {
            names.Add(property.Name);
            if (property.Value.TryGetProperty("items", out var items)
                && items.TryGetProperty("properties", out var itemProperties))
            {
                names.AddRange(itemProperties.EnumerateObject().Select(item => item.Name));
            }
        }

        return names;
    }

    private static (IReadOnlyList<string> Required, IReadOnlyList<string> Optional) ItemFields()
    {
        using var document = JsonDocument.Parse(ExtractionSchema.Json);
        var items = document.RootElement
            .GetProperty("properties")
            .GetProperty("clauses")
            .GetProperty("items");

        var required = items.GetProperty("required")
            .EnumerateArray()
            .Select(entry => entry.GetString()!)
            .ToArray();
        var optional = items.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Except(required)
            .ToArray();

        return (required, optional);
    }
}
