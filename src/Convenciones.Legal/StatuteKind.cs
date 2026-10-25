namespace Convenciones.Legal;

/// <summary>
/// What kind of statute a legal-library entry is — the discriminator that folds the legacy
/// <c>ley_trabajo</c> and <c>otras_leyes</c> tables into one entity (Q-6).
/// </summary>
/// <remarks>
/// <para>
/// The database type is <c>app.statute_kind</c> with the labels <c>LabourLaw</c> and
/// <c>OtherStatute</c>; the names here are the labels, mapped pass-through in <c>Program.cs</c>. No
/// numeric value may leak into JSON — responses serialise the name.
/// </para>
/// <para>
/// Only one <see cref="LabourLaw"/> row may exist, enforced by
/// <c>statute_single_labour_law_key</c>. The legacy system loaded the labour statute with
/// <c>realcount() == 1</c> and silently returned false when a second row appeared, which broke the
/// whole labour-law screen.
/// </para>
/// </remarks>
public enum StatuteKind
{
    /// <summary>The single labour statute. At most one may exist.</summary>
    LabourLaw = 0,

    /// <summary>Any other statute of the library. Unrestricted in number.</summary>
    OtherStatute = 1,
}
