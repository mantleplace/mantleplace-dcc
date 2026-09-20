namespace MantlePlace.Revit.Core;

/// <summary>
/// What a tree point is, as the platform classified it.
/// </summary>
/// <remarks>
/// A <em>tree point</em> is any published point of the tree layer; <see cref="Tree"/> and
/// <see cref="Shrub"/> name only its foliage type. The distinction arrives in the bundle and is
/// never derived here — see <see cref="FoliageTypes"/>.
/// </remarks>
public enum FoliageType
{
    /// <summary>The default, and what every value this add-in does not know reads as.</summary>
    Tree = 0,

    /// <summary>Published where the ESA WorldCover class under the point is Shrubland (20).</summary>
    Shrub = 1,
}

/// <summary>
/// Reads the published <c>foliage_type</c> value. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary is <strong>closed and owned by the platform</strong> (<c>spec/format.md</c> §4.4).
/// This add-in maps each value to a family and derives nothing: ⛔ a foliage type is never inferred
/// from <c>height_m</c> or <c>crown_radius_m</c>. A shrub can be taller than a young tree, and the
/// classification comes from land cover under the point rather than from its size.
/// </para>
/// <para>
/// ⛔ A value this add-in does not know reads as <see cref="FoliageType.Tree"/>. The vocabulary grows
/// by gaining values, and a new value is a new vocabulary version — never a change of meaning for an
/// existing one. So a vocabulary id this build does not know is no reason to ignore the column: the
/// values it knows keep their meaning and are mapped.
/// </para>
/// <para>
/// ⚠️ <c>shrub</c> means something else elsewhere in this codebase. As a land-cover polygon
/// <em>subtype</em> it selects the ground material phrase <c>tall grass</c>
/// (<see cref="RendererKeywords"/>) — a surface under a canopy, not a plant. Both words come from
/// the platform and neither is ours to rename; see <c>CONTEXT.md</c>.
/// </para>
/// </remarks>
public static class FoliageTypes
{
    /// <summary>The CSV header this add-in reads the foliage type from.</summary>
    public const string Column = "foliage_type";

    /// <summary>
    /// The one vocabulary this build knows, as <c>landcover.tree_points.foliage_type_vocabulary</c>
    /// publishes it.
    /// </summary>
    public const string KnownVocabulary = "1";

    private const string ShrubValue = "shrub";
    private const string TreeValue = "tree";

    /// <summary>True when <paramref name="vocabulary"/> is one this build was taught.</summary>
    public static bool IsKnownVocabulary(string? vocabulary) =>
        string.Equals(vocabulary, KnownVocabulary, StringComparison.Ordinal);

    /// <summary>
    /// Maps one published cell.
    /// </summary>
    /// <remarks>
    /// Matched with <see cref="StringComparison.Ordinal"/>, so <c>Shrub</c> and <c>shrubland</c> are
    /// unknown values rather than near misses. The vocabulary is closed: accepting a value the
    /// platform did not publish would be this add-in inventing one.
    /// </remarks>
    /// <param name="cell">The cell as published, or <c>null</c> where the row had no such field.</param>
    /// <param name="reading">How the cell was read, which the log counts.</param>
    public static FoliageType Read(string? cell, out FoliageReading reading)
    {
        string value = cell?.Trim() ?? string.Empty;

        if (value.Length == 0)
        {
            reading = FoliageReading.Empty;
            return FoliageType.Tree;
        }

        if (string.Equals(value, ShrubValue, StringComparison.Ordinal))
        {
            reading = FoliageReading.Known;
            return FoliageType.Shrub;
        }

        if (string.Equals(value, TreeValue, StringComparison.Ordinal))
        {
            reading = FoliageReading.Known;
            return FoliageType.Tree;
        }

        reading = FoliageReading.Unknown;
        return FoliageType.Tree;
    }
}

/// <summary>How one <c>foliage_type</c> cell was read — the three outcomes a log distinguishes.</summary>
public enum FoliageReading
{
    /// <summary>A value in the vocabulary this build knows.</summary>
    Known = 0,

    /// <summary>No cell, or an empty one: the ETL did not classify the point.</summary>
    Empty = 1,

    /// <summary>A value this build does not know: the vocabulary has moved on.</summary>
    Unknown = 2,
}
