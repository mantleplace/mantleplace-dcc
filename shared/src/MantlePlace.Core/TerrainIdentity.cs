using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct ExistingTerrain(long ElementId, string? Comments);

public enum TerrainDisposition
{
    Create,

    Reuse,

    RefuseStale,
}

public sealed class TerrainDecision
{
    public required TerrainDisposition Disposition { get; init; }

    public long ExistingElementId { get; init; }

    public string Stamp { get; init; } = string.Empty;

    public string Explanation { get; init; } = string.Empty;
}

public static class TerrainIdentity
{
    private const string Prefix = "Mantle Place Terrain ";

    private const string DatumSeparator = "; heights in ";

    public const string UnknownBuild = "unknown";

    private const int BuildTokenLength = 12;

    public static string Stamp(string cacheKeyStem, string? artifactSha256, string? verticalDatum = null)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return Prefix + cacheKeyStem + "/" + BuildToken(artifactSha256)
            + (string.IsNullOrEmpty(verticalDatum) ? string.Empty : DatumSeparator + verticalDatum);
    }

    public static bool IsStampFor(string? comments, string cacheKeyStem)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        string prefix = Prefix + cacheKeyStem + "/";
        return comments is not null
            && comments.StartsWith(prefix, StringComparison.Ordinal)
            && comments.Length > prefix.Length;
    }

    public static bool IsStamp(string? comments)
        => comments is not null
            && comments.StartsWith(Prefix, StringComparison.Ordinal)
            && comments.Length > Prefix.Length;

    public static string? StemOf(string? comments)
    {
        if (!IsStamp(comments))
        {
            return null;
        }

        string rest = comments![Prefix.Length..];
        int slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? rest[..slash] : null;
    }

    public static string? BuildTokenOf(string? comments, string cacheKeyStem)
        => Halves(comments, cacheKeyStem)?.Build;

    public static string? DatumOf(string? comments, string cacheKeyStem)
        => Halves(comments, cacheKeyStem)?.Datum;

    private static (string Build, string? Datum)? Halves(string? comments, string cacheKeyStem)
    {
        if (!IsStampFor(comments, cacheKeyStem))
        {
            return null;
        }

        string rest = comments![(Prefix + cacheKeyStem + "/").Length..];
        int separator = rest.IndexOf(DatumSeparator, StringComparison.Ordinal);
        if (separator < 0)
        {
            return (rest, null);
        }

        string datum = rest[(separator + DatumSeparator.Length)..];
        return (rest[..separator], datum.Length == 0 ? null : datum);
    }

    public static TerrainDecision Decide(
        IReadOnlyList<ExistingTerrain> grounds,
        string cacheKeyStem,
        string? artifactSha256,
        string? verticalDatum = null)
    {
        ArgumentNullException.ThrowIfNull(grounds);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        string wanted = BuildToken(artifactSha256);
        string stamp = Stamp(cacheKeyStem, artifactSha256, verticalDatum);

        int ours = 0;
        ExistingTerrain? first = null;
        foreach (ExistingTerrain ground in grounds)
        {
            if (IsStampFor(ground.Comments, cacheKeyStem))
            {
                ours++;
                first ??= ground;
            }
        }

        if (first is not { } mine)
        {
            return new TerrainDecision
            {
                Disposition = TerrainDisposition.Create,
                Stamp = stamp,

                Explanation = grounds.Count == 0
                    ? string.Empty
                    : $"This project already has {Strangers(grounds.Count)} — a curator's own, another "
                        + "order's, or one from an import made before terrain stamping. Nothing was "
                        + "touched, and this bundle's terrain was built alongside it.",
            };
        }

        string found = BuildTokenOf(mine.Comments, cacheKeyStem)!;
        string duplicates = ours > 1
            ? $" ({Number(ours)} ground toposolids here carry this bundle's stamp, which an import "
                + "before this version could produce; the first is the one used.)"
            : string.Empty;

        if (!string.Equals(found, wanted, StringComparison.Ordinal))
        {
            return new TerrainDecision
            {
                Disposition = TerrainDisposition.RefuseStale,
                ExistingElementId = mine.ElementId,
                Stamp = stamp,
                Explanation =
                    "The terrain in this project came from an EARLIER build of this bundle, so no second "
                    + "ground was created on top of it — and it was not deleted, because a curator's "
                    + "buildings and views may be standing on it. To take the new surface, delete "
                    + $"toposolid {Number(mine.ElementId)} and import again."
                    + duplicates,
            };
        }

        return new TerrainDecision
        {
            Disposition = TerrainDisposition.Reuse,
            ExistingElementId = mine.ElementId,
            Stamp = stamp,
            Explanation =
                "This bundle's terrain is already in this project from an earlier import, so it was left "
                + "alone and no second ground was built."
                + (string.Equals(wanted, UnknownBuild, StringComparison.Ordinal)
                    ? " This bundle declares no digest for its surface, so a rebuilt surface would not "
                        + "have been noticed here."
                    : string.Empty)
                + duplicates,
        };
    }

    internal static string BuildToken(string? artifactSha256)
    {
        if (string.IsNullOrWhiteSpace(artifactSha256))
        {
            return UnknownBuild;
        }

        string trimmed = artifactSha256.Trim().ToLowerInvariant();
        return trimmed.Length <= BuildTokenLength ? trimmed : trimmed[..BuildTokenLength];
    }

    private static string Strangers(int howMany)
        => howMany == 1
            ? "a ground toposolid that is not this bundle's"
            : $"{Number(howMany)} ground toposolids that are not this bundle's";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
