using System.Text.Json;
using System.Text.Json.Nodes;
using Convenciones.Worker.Extraction;
using Json.Schema;
using Shouldly;
using Xunit;

namespace Convenciones.UnitTests.Extraction;

/// <summary>
/// The classification request is sent in JSON object mode, so the provider never receives
/// <see cref="ClassificationSchema.Json"/>: the prompt is the only place the model can learn the
/// contract. These tests derive their assertions from the schema so the two cannot drift apart.
/// </summary>
public sealed class ClassificationPromptContractTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(ClassificationSchema.Json);

    [Fact]
    public void Embedded_example_satisfies_the_classification_schema()
    {
        Evaluate(JsonNode.Parse(ClassificationPrompt.ExampleJson)!).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void System_prompt_embeds_the_exact_validated_example()
    {
        ClassificationPrompt.System.ShouldContain(ClassificationPrompt.ExampleJson);
    }

    [Fact]
    public void System_prompt_names_every_property_the_schema_defines()
    {
        foreach (var name in PropertyNames())
        {
            ClassificationPrompt.System.ShouldContain($"\"{name}\"");
        }
    }

    [Fact]
    public void System_prompt_documents_the_contract_instead_of_pointing_to_an_unavailable_schema()
    {
        ClassificationPrompt.System.ShouldNotContain("esquema");
        ClassificationPrompt.System.ShouldContain("Contrato de campos");
    }

    [Fact]
    public void System_prompt_forbids_additional_properties()
    {
        ClassificationPrompt.System.ShouldContain("sin propiedades adicionales");
        ClassificationPrompt.System.ShouldContain("No añadas ninguna otra propiedad");
    }

    [Fact]
    public void Required_fields_are_documented_as_obligatory_and_optional_fields_as_nullable()
    {
        var (required, optional) = RequiredAndOptionalFields();

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
        ContractLine("classifications").ShouldContain("array");
        ContractLine("categories").ShouldContain("array");
        ContractLine("clause_ordinal").ShouldContain("mayor o igual que 1");
        ContractLine("confidence").ShouldContain("entre 0 y 1");
        ContractLine("rationale").ShouldContain("240");
        // The taxonomy is supplied at runtime in the user message; the prompt must bind keys to it.
        ContractLine("key").ShouldContain("taxonomía");
    }

    [Fact]
    public void The_validator_still_rejects_an_extra_property()
    {
        var instance = JsonNode.Parse(ClassificationPrompt.ExampleJson)!.AsObject();
        instance["classifications"]!.AsArray()[0]!.AsObject()["extra"] = "value";

        Evaluate(instance).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void The_validator_still_requires_the_ordinal_and_categories()
    {
        var instance = JsonNode.Parse("""{"classifications":[{"primary":"pricing"}]}""")!;

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
        var line = ClassificationPrompt.System
            .Split('\n')
            .Select(candidate => candidate.Trim())
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));

        line.ShouldNotBeNull($"the prompt has no contract line for '{property}'");
        return line;
    }

    private static IEnumerable<string> PropertyNames()
    {
        using var document = JsonDocument.Parse(ClassificationSchema.Json);
        var names = new List<string>();
        CollectProperties(document.RootElement, names);
        return names;
    }

    private static void CollectProperties(JsonElement schema, List<string> names)
    {
        if (schema.ValueKind is not JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties))
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (!names.Contains(property.Name))
            {
                names.Add(property.Name);
            }

            CollectProperties(property.Value, names);
            if (property.Value.TryGetProperty("items", out var items))
            {
                CollectProperties(items, names);
            }
        }
    }

    private static (IReadOnlyList<string> Required, IReadOnlyList<string> Optional) RequiredAndOptionalFields()
    {
        using var document = JsonDocument.Parse(ClassificationSchema.Json);
        var required = new List<string>();
        CollectRequired(document.RootElement, required);

        var optional = PropertyNames().Where(name => !required.Contains(name)).ToArray();
        return (required, optional);
    }

    private static void CollectRequired(JsonElement schema, List<string> required)
    {
        if (schema.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("required", out var entries))
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var name = entry.GetString();
                if (name is not null && !required.Contains(name))
                {
                    required.Add(name);
                }
            }
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                CollectRequired(property.Value, required);
                if (property.Value.TryGetProperty("items", out var items))
                {
                    CollectRequired(items, required);
                }
            }
        }
    }
}
