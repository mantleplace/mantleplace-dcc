namespace MantlePlace.Core;

public enum FoliageType
{
    Tree = 0,

    Shrub = 1,
}

public static class FoliageTypes
{
    public const string Column = "foliage_type";

    public const string KnownVocabulary = "1";

    private const string ShrubValue = "shrub";
    private const string TreeValue = "tree";

    public static bool IsKnownVocabulary(string? vocabulary) =>
        string.Equals(vocabulary, KnownVocabulary, StringComparison.Ordinal);

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

public enum FoliageReading
{
    Known = 0,

    Empty = 1,

    Unknown = 2,
}
