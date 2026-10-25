using Npgsql;

namespace Convenciones.Documents;

/// <summary>
/// Maps CLR names to PostgreSQL labels verbatim.
/// </summary>
/// <remarks>
/// Npgsql's default translator converts <c>NotRequested</c> to <c>not_requested</c>, but the labels of
/// <c>platform.extraction_status</c> are PascalCase. Without this translator the insert fails with
/// <c>22P02: invalid input value for enum platform.extraction_status: "not_requested"</c>.
/// The database schema is the authority here, so the mapping adapts to it rather than the reverse.
/// </remarks>
public sealed class ExactNameTranslator : INpgsqlNameTranslator
{
    public static ExactNameTranslator Instance { get; } = new();

    public string TranslateTypeName(string clrName) => clrName;

    public string TranslateMemberName(string clrName) => clrName;
}
