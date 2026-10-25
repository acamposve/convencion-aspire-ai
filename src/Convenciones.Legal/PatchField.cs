using System.Text.Json;
using System.Text.Json.Serialization;

namespace Convenciones.Legal;

/// <summary>
/// One field of a partial update. Distinguishes "not sent" from "sent as null", which a plain
/// nullable value cannot: the first leaves the stored value alone, the second clears a nullable field.
/// </summary>
/// <remarks>
/// <para>
/// A property that is absent from the JSON body leaves the field <see cref="Unset"/>; a property that
/// is present — including as <c>null</c> — arrives <see cref="Set"/>. That is the whole mechanism, and
/// it is what makes <c>PATCH</c> honest: a rename cannot silently erase a publication date, and a
/// nullable field can still be cleared deliberately.
/// </para>
/// <para>
/// Serialisation writes the value itself, and an unset field is omitted through
/// <see cref="JsonIgnoreCondition.WhenWritingDefault"/>, so the request type is also usable from a
/// test or a client without a special wire format.
/// </para>
/// </remarks>
[JsonConverter(typeof(PatchFieldConverterFactory))]
public readonly record struct PatchField<T>(bool IsSet, T? Value)
{
    /// <summary>The field was not present in the request.</summary>
    public static PatchField<T> Unset => default;

    /// <summary>The field was present, with this value (<see langword="null"/> means "clear").</summary>
    public static PatchField<T> Set(T? value) => new(true, value);
}

public sealed class PatchFieldConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(PatchField<>);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        ArgumentNullException.ThrowIfNull(options);

        var valueType = typeToConvert.GetGenericArguments()[0];

        return (JsonConverter)Activator.CreateInstance(
            typeof(PatchFieldConverter<>).MakeGenericType(valueType),
            options)!;
    }
}

public sealed class PatchFieldConverter<T> : JsonConverter<PatchField<T>>
{
    public PatchFieldConverter(JsonSerializerOptions options) => Options = options;

    /// <summary>
    /// Required, or the serializer would skip the converter for a JSON <c>null</c> and leave the
    /// property at its default — which is indistinguishable from "not sent" and would turn an
    /// explicit clear into a silent no-op.
    /// </summary>
    public override bool HandleNull => true;

    private JsonSerializerOptions Options { get; }

    public override PatchField<T> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        PatchField<T>.Set(JsonSerializer.Deserialize<T>(ref reader, Options));

    public override void Write(
        Utf8JsonWriter writer,
        PatchField<T> value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        JsonSerializer.Serialize(writer, value.Value, Options);
    }
}
