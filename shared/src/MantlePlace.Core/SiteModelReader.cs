using System.Text;

namespace MantlePlace.Core;

public sealed class SiteModelContents
{
    public IReadOnlyList<string> BuildingGlobalIds { get; init; } = [];

    public int TerrainElements { get; init; }

    public int UnidentifiedBuildings { get; init; }
}

public static class SiteModelReader
{
    private const string BuildingEntity = "IFCBUILDINGELEMENTPROXY";
    private const string TerrainEntity = "IFCGEOGRAPHICELEMENT";

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
