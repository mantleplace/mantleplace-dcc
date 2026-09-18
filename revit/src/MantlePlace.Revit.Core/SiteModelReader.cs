using System.Text;

namespace MantlePlace.Revit.Core;

/// <summary>What the site model holds that the context-building step cares about.</summary>
public sealed class SiteModelContents
{
    /// <summary>The GlobalId of every building in the site model, in file order, each once.</summary>
    public IReadOnlyList<string> BuildingGlobalIds { get; init; } = [];

    /// <summary>How many context-terrain elements the site model carries — seen, and never copied.</summary>
    public int TerrainElements { get; init; }

    /// <summary>Buildings that carry no GlobalId, and so cannot be found again or stamped.</summary>
    public int UnidentifiedBuildings { get; init; }
}

/// <summary>
/// Reads which elements of the site model are context buildings, from the IFC's own text. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The site model carries one <c>IfcBuildingElementProxy</c> per building, each with its own
/// extrusion, and one <c>IfcGeographicElement</c> for the context terrain. Which is which is decided
/// here, from the entity the platform published, rather than in the shim from the category Revit's
/// IFC import happens to file each one under: that mapping is Revit's, it is not the bundle's, and
/// a building that came back as a Site element would otherwise vanish without a test noticing. The
/// shim finds each element again by the GlobalId Revit's import records on it.
/// </para>
/// <para>
/// ⛔ The terrain is never a building. The project already has the terrain as a toposolid, built
/// from the surface artifact; copying the site model's own mesh of it would lay a second ground,
/// unselectable as ground, over the first.
/// </para>
/// <para>
/// This is an ISO 10303-21 scan and no more: records split on the <c>;</c> that ends them outside a
/// string or a comment, and the first attribute of a building record read as its GlobalId. It does
/// not resolve references or read geometry — the geometry is Revit's to import, verbatim.
/// </para>
/// </remarks>
public static class SiteModelReader
{
    private const string BuildingEntity = "IFCBUILDINGELEMENTPROXY";
    private const string TerrainEntity = "IFCGEOGRAPHICELEMENT";

    /// <summary>Reads the site model's buildings, or says why the text is not a site model.</summary>
    /// <returns><c>null</c> on success; otherwise one sentence for the import log.</returns>
    public static string? TryRead(string stepText, out SiteModelContents contents)
    {
        ArgumentNullException.ThrowIfNull(stepText);

        contents = new SiteModelContents();
        if (!stepText.TrimStart().StartsWith("ISO-10303-21", StringComparison.Ordinal))
        {
            return "The site model is not an IFC file (it does not begin \"ISO-10303-21\"), so no "
                + "context buildings were read from it.";
        }

        int data = DataSectionStart(stepText);
        if (data < 0)
        {
            return "The site model has no DATA section, so it holds no elements and no context "
                + "buildings were read from it.";
        }

        List<string> buildings = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int terrain = 0;
        int unidentified = 0;

        foreach (Range record in Records(stepText, data))
        {
            ReadOnlySpan<char> text = stepText.AsSpan(record).Trim();
            if (EntityOf(text, out int open) is not { } entity)
            {
                continue;
            }

            if (entity.Equals(TerrainEntity, StringComparison.OrdinalIgnoreCase))
            {
                terrain++;
            }
            else if (entity.Equals(BuildingEntity, StringComparison.OrdinalIgnoreCase))
            {
                if (FirstStringAttribute(text[(open + 1)..]) is { Length: > 0 } globalId)
                {
                    if (seen.Add(globalId))
                    {
                        buildings.Add(globalId);
                    }
                }
                else
                {
                    unidentified++;
                }
            }
        }

        contents = new SiteModelContents
        {
            BuildingGlobalIds = buildings,
            TerrainElements = terrain,
            UnidentifiedBuildings = unidentified,
        };
        return null;
    }

    /// <summary>Where the first record after <c>DATA;</c> begins, or <c>-1</c>.</summary>
    private static int DataSectionStart(string text)
    {
        foreach (Range record in Records(text, 0))
        {
            if (text.AsSpan(record).Trim().Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                return record.End.Value + 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Each record from <paramref name="start"/> on, without its terminating <c>;</c>.
    /// </summary>
    /// <remarks>
    /// A quote toggles the in-string state, which is also right for STEP's doubled-quote escape: the
    /// two quotes of <c>''</c> leave and re-enter the string. A comment is <c>/* … */</c> outside a
    /// string, and a <c>;</c> inside one ends nothing.
    /// </remarks>
    private static IEnumerable<Range> Records(string text, int start)
    {
        int recordStart = start;
        bool inString = false;
        for (int index = start; index < text.Length; index++)
        {
            char c = text[index];
            if (inString)
            {
                inString = c != '\'';
                continue;
            }

            if (c == '\'')
            {
                inString = true;
            }
            else if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                int close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                int after = close < 0 ? text.Length : close + 2;

                // A comment before a record is nobody's, so the record starts after it. One inside
                // a record stays in its text, where nothing this reader reads can be reached by it.
                if (text.AsSpan(recordStart, index - recordStart).IsWhiteSpace())
                {
                    recordStart = after;
                }

                index = after - 1;
            }
            else if (c == ';')
            {
                yield return new Range(recordStart, index);
                recordStart = index + 1;
            }
        }
    }

    /// <summary>
    /// The entity keyword of an instance record — a hash and an instance number, <c>=</c>, then
    /// <c>IFCBUILDINGELEMENTPROXY(…)</c> — and where its attribute list opens, or <c>null</c> for any
    /// other record.
    /// </summary>
    private static string? EntityOf(ReadOnlySpan<char> record, out int open)
    {
        open = -1;
        if (record.Length == 0 || record[0] != '#')
        {
            return null;
        }

        int equals = record.IndexOf('=');
        open = record.IndexOf('(');
        if (equals < 0 || open < equals)
        {
            return null;
        }

        ReadOnlySpan<char> keyword = record[(equals + 1)..open].Trim();
        return keyword.Length == 0 ? null : keyword.ToString();
    }

    /// <summary>
    /// The first attribute when it is a string — a GlobalId — or <c>null</c> when it is <c>$</c> or
    /// anything else.
    /// </summary>
    private static string? FirstStringAttribute(ReadOnlySpan<char> attributes)
    {
        ReadOnlySpan<char> rest = attributes.TrimStart();
        if (rest.Length == 0 || rest[0] != '\'')
        {
            return null;
        }

        StringBuilder value = new();
        for (int index = 1; index < rest.Length; index++)
        {
            if (rest[index] != '\'')
            {
                value.Append(rest[index]);
            }
            else if (index + 1 < rest.Length && rest[index + 1] == '\'')
            {
                value.Append('\'');
                index++;
            }
            else
            {
                return value.ToString();
            }
        }

        return null;
    }
}
