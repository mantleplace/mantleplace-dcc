using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct NewSiteBoundary(int Ordinal, string Stamp);

public enum GroundLayer
{
    LandUse,

    LandCover,

    Water,

    RoadSurface,
}

public readonly record struct GroundStamp(GroundLayer Layer, string Token);

public sealed record GroundLayerWords(string StampKind, string MaterialKind, string Label, string Noun, string? MaterialWord)
{
    public static GroundLayerWords For(GroundLayer layer) => layer switch
    {
        GroundLayer.LandUse => new("Site Boundary", "boundary", SiteVectorLayers.LandUse.Label, "site boundary", MaterialWord: null),
        GroundLayer.LandCover => new("Land Cover", "land cover", SiteVectorLayers.LandCover.Label, "land cover", MaterialWord: null),
        GroundLayer.Water => new("Water", "water body", SiteVectorLayers.Water.Label, "water", "water"),
        GroundLayer.RoadSurface => new("Road Surface", "road surface", SiteVectorLayers.RoadPolygons.Label, "road", "asphalt"),
        _ => throw new ArgumentOutOfRangeException(
            nameof(layer),
            layer,
            "This layer has no words, so its subdivisions have no identity of their own."),
    };
}

public static class SiteBoundaryIdentity
{
    public static string Stamp(GroundLayer layer, string cacheKeyStem, string? featureName, int oneBasedIndex)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return OwnedPrefix(layer, cacheKeyStem) + TokenOf(featureName, oneBasedIndex);
    }

    public static IReadOnlyList<string> Stamps(
        GroundLayer layer,
        IReadOnlyList<string?> featureNames,
        string cacheKeyStem)
    {
        ArgumentNullException.ThrowIfNull(featureNames);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        string prefix = OwnedPrefix(layer, cacheKeyStem);
        return [.. DistinctTokens(featureNames).Select(token => prefix + token)];
    }

    public static IReadOnlyList<NewSiteBoundary> NewFeatures(
        GroundLayer layer,
        IReadOnlyCollection<string> existingStamps,
        IReadOnlyList<string?> featureNames,
        string cacheKeyStem)
    {
        ArgumentNullException.ThrowIfNull(existingStamps);

        HashSet<string> existing = new(existingStamps, StringComparer.Ordinal);
        IReadOnlyList<string> stamps = Stamps(layer, featureNames, cacheKeyStem);

        List<NewSiteBoundary> created = [];
        for (int index = 0; index < stamps.Count; index++)
        {
            if (!existing.Contains(stamps[index]))
            {
                created.Add(new NewSiteBoundary(index + 1, stamps[index]));
            }
        }

        return created;
    }

    public static string Batch(IReadOnlyList<string> stamps)
    {
        ArgumentNullException.ThrowIfNull(stamps);
        return string.Join(MemberBreak, stamps);
    }

    public static IReadOnlyList<string> Members(string? comments)
        => string.IsNullOrEmpty(comments) ? [] : comments.Split(MemberBreak);

    public static bool IsStampFor(string? comments, string cacheKeyStem) => Parse(comments, cacheKeyStem) is not null;

    public static GroundStamp? Parse(string? comments, string cacheKeyStem)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        if (comments is null)
        {
            return null;
        }

        comments = comments.Split(MemberBreak)[0];

        foreach (GroundLayer layer in Layers)
        {
            string prefix = OwnedPrefix(layer, cacheKeyStem);
            if (comments.StartsWith(prefix, StringComparison.Ordinal) && comments.Length > prefix.Length)
            {
                return new GroundStamp(layer, comments[prefix.Length..]);
            }
        }

        return null;
    }

    public static GroundStamp NamingStamp(string? comments, string? stampCutWith, long elementId, string cacheKeyStem)
        => Parse(comments, cacheKeyStem)
            ?? Parse(stampCutWith, cacheKeyStem)
            ?? new GroundStamp(GroundLayer.LandUse, elementId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private const char MemberBreak = '\n';

    private static readonly GroundLayer[] Layers = Enum.GetValues<GroundLayer>();

    private static string OwnedPrefix(GroundLayer layer, string cacheKeyStem)
        => "Mantle Place " + GroundLayerWords.For(layer).StampKind + " " + cacheKeyStem + "/";

    private static IReadOnlyList<string> DistinctTokens(IReadOnlyList<string?> featureNames)
    {
        string[] tokens = new string[featureNames.Count];
        bool[] indexed = new bool[featureNames.Count];
        for (int index = 0; index < featureNames.Count; index++)
        {
            tokens[index] = TokenOf(featureNames[index], index + 1);
            indexed[index] = IsBlank(featureNames[index]);
        }

        bool collided = true;
        while (collided)
        {
            collided = false;
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            foreach (string token in tokens)
            {
                counts[token] = counts.TryGetValue(token, out int count) ? count + 1 : 1;
            }

            for (int index = 0; index < tokens.Length; index++)
            {
                if (!indexed[index] && counts[tokens[index]] > 1)
                {
                    tokens[index] = tokens[index] + " " + IndexText(index + 1);
                    indexed[index] = true;
                    collided = true;
                }
            }
        }

        return tokens;
    }

    private static string TokenOf(string? featureName, int oneBasedIndex)
        => IsBlank(featureName) ? IndexText(oneBasedIndex) : OneLine(featureName!).Trim();

    private static string OneLine(string featureName)
        => featureName.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace(MemberBreak, ' ');

    private static bool IsBlank(string? featureName) => string.IsNullOrWhiteSpace(featureName);

    private static string IndexText(int oneBasedIndex)
        => oneBasedIndex.ToString(CultureInfo.InvariantCulture);
}
