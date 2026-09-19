using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One site-boundary feature the current import still has to create.</summary>
/// <param name="Ordinal">The feature's ONE-based position in the layer, the same number the stamp carries.</param>
/// <param name="Stamp">The identity written to the subdivision's Comments parameter.</param>
public readonly record struct NewSiteBoundary(int Ordinal, string Stamp);

/// <summary>Which published polygon layer a subdivision was cut from.</summary>
/// <remarks>
/// The layer is the stamp's kind, so it is part of the identity: both layers stamp an unnamed
/// feature by its position, and without the kind land-use feature 1 would read as land-cover
/// feature 1 already cut. <c>land_use</c> and <c>land_cover</c> are two different Overture layers,
/// not two names for one — a bundle can carry either, both or neither.
/// </remarks>
public enum GroundLayer
{
    /// <summary><c>vector.layers[name=="land_use"]</c>. Stamped <c>Mantle Place Site Boundary</c>.</summary>
    LandUse,

    /// <summary><c>vector.layers[name=="land_cover"]</c>. Stamped <c>Mantle Place Land Cover</c>.</summary>
    LandCover,
}

/// <summary>An owned subdivision stamp, taken apart: which layer, and the per-feature token.</summary>
public readonly record struct GroundStamp(GroundLayer Layer, string Token);

/// <summary>
/// Everything that is spelled differently for the two layers, in one place, so a third layer is one
/// row rather than a hunt for every <c>if</c> on the layer.
/// </summary>
/// <param name="StampKind">The stamp's kind: <c>Mantle Place {StampKind} {stem}/{token}</c>.</param>
/// <param name="MaterialKind">The word a per-subdivision drape material carries before its token.</param>
/// <param name="Label">The layer in a log sentence, plural: "Skipped the {Label}".</param>
/// <param name="Noun">The layer as an adjective on "subdivision(s)".</param>
public sealed record GroundLayerWords(string StampKind, string MaterialKind, string Label, string Noun)
{
    /// <summary>The words for <paramref name="layer"/>.</summary>
    /// <remarks>
    /// Land use keeps the spellings it had before land cover existed — the stamp's kind and the
    /// material's word are identity, and changing either would have a re-import miss every
    /// subdivision and material an earlier import made.
    /// </remarks>
    public static GroundLayerWords For(GroundLayer layer) => layer switch
    {
        GroundLayer.LandCover => new("Land Cover", "land cover", "land cover", "land cover"),
        _ => new("Site Boundary", "boundary", "site boundaries", "site boundary"),
    };
}

/// <summary>
/// Decides which site-boundary features an import still has to create, and what identity each new
/// subdivision is stamped with.
/// </summary>
/// <remarks>
/// <para>
/// A subdivision has no name slot, so the <c>Material</c>/<c>ToposolidType</c> precedent —
/// name-from-cache-key, reuse when found — lands in its instance Comments parameter instead. The
/// stamp is <c>Mantle Place Site Boundary {stem}/{feature}</c> for a land-use polygon and
/// <c>Mantle Place Land Cover {stem}/{feature}</c> for a land-cover one: the stem scopes it to one bundle, so
/// a boundary from a different order never reads as "already present", and the feature half is the
/// GeoJSON name where there is one and the one-based position where there is not.
/// </para>
/// <para>
/// Geometric identity — comparing curve loops against what is already on the terrain — was rejected:
/// a subdivision's profile is projected onto the relief, so the loops Revit hands back are not the
/// flat z=0 loops this import drew, and comparing them means new API surface plus a tolerance nobody
/// can defend. A string in a parameter is decidable here, headlessly.
/// </para>
/// <para>
/// This lives in the pure core for the reason <c>ImportStepKinds.LifetimeOf</c> does: the create/skip
/// decision is policy, and policy in the shim is covered by nothing but review (HPS-02).
/// </para>
/// </remarks>
public static class SiteBoundaryIdentity
{
    /// <summary>The stamp for one feature, before any list-level disambiguation.</summary>
    /// <remarks>
    /// A null or blank name falls back to the one-based position. Duplicate names are the caller's
    /// list-level problem and are resolved by <see cref="NewFeatures"/>, which suffixes the position
    /// — one feature alone cannot know its name is shared.
    /// </remarks>
    public static string Stamp(GroundLayer layer, string cacheKeyStem, string? featureName, int oneBasedIndex)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return OwnedPrefix(layer, cacheKeyStem) + TokenOf(featureName, oneBasedIndex);
    }

    /// <summary>
    /// Every feature's stamp, in layer order, whether or not its subdivision already exists.
    /// </summary>
    /// <remarks>
    /// What pairs a subdivision on the terrain with the published feature it was cut from, on an
    /// import that cut it and on one that found it already there alike.
    /// </remarks>
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

    /// <summary>
    /// The features the current import still has to create: every feature whose stamp is not already
    /// present, each paired with the stamp its subdivision must carry.
    /// </summary>
    /// <remarks>
    /// Comparison is ordinal over the FULL stamp, so a stamp written by a different bundle's stem can
    /// never suppress a creation here. An empty <paramref name="existingStamps"/> — the first import —
    /// returns every feature.
    /// </remarks>
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

    /// <summary>
    /// Whether a subdivision's Comments string is a stamp THIS plugin wrote for THIS bundle, from
    /// either layer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drape needs this because the material goes on each subdivision itself — on the instance in
    /// Revit 2025, where a subdivision is typeless, and through a type of its own in 2026 and 2027,
    /// where it is typed (<see cref="SubDivisionMaterial"/>) — so the instance has to be found. Finding it by
    /// stamp rather than by an id remembered from this session is what makes a RE-import able to
    /// repair un-draped patches — the remembered list is empty whenever the boundaries already exist,
    /// so a second import used to drape nothing at all while reporting success.
    /// </para>
    /// <para>
    /// ⛔ <b>The stem half is what keeps the trespass rule.</b> A curator's own subdivision carries no
    /// stamp, and another order's carries a different stem, so neither can match. That is the same
    /// line this plugin draws when it declines to edit the project's own toposolid type: touch what
    /// this import owns, and nothing else.
    /// </para>
    /// </remarks>
    public static bool IsStampFor(string? comments, string cacheKeyStem) => Parse(comments, cacheKeyStem) is not null;

    /// <summary>
    /// A stamp this import owns, taken apart into its layer and its per-feature token — the part
    /// after the stem — or <c>null</c> for any other Comments.
    /// </summary>
    /// <remarks>
    /// The token is what names a subdivision's own drape material, so a re-import finds the material
    /// it made rather than growing another; the layer keeps the two layers' materials apart.
    /// </remarks>
    public static GroundStamp? Parse(string? comments, string cacheKeyStem)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        if (comments is null)
        {
            return null;
        }

        foreach (GroundLayer layer in Layers)
        {
            // Ordinal over the whole prefix INCLUDING the separator. Without the trailing "/", stem
            // "abc" would claim stem "abcdef"'s subdivisions — and cache-key stems are truncated
            // hashes, where one being a prefix of another is a collision waiting rather than a
            // hypothetical.
            string prefix = OwnedPrefix(layer, cacheKeyStem);
            if (comments.StartsWith(prefix, StringComparison.Ordinal) && comments.Length > prefix.Length)
            {
                return new GroundStamp(layer, comments[prefix.Length..]);
            }
        }

        return null;
    }

    private static readonly GroundLayer[] Layers = [GroundLayer.LandUse, GroundLayer.LandCover];

    /// <summary>Everything a stamp of this layer and bundle starts with, separator included.</summary>
    private static string OwnedPrefix(GroundLayer layer, string cacheKeyStem)
        => "Mantle Place " + GroundLayerWords.For(layer).StampKind + " " + cacheKeyStem + "/";

    /// <summary>
    /// One token per feature, guaranteed pairwise distinct: two features named "Zone A" must not
    /// share a stamp, or the second import would recreate whichever one lost the race.
    /// </summary>
    /// <remarks>
    /// The rule: a blank name is its one-based position; a name shared with another feature (or
    /// colliding with any other token, however it arose — a feature literally named "Zone A 2" next
    /// to a duplicated "Zone A") gets the position appended. Position-suffixed tokens end in their
    /// own distinct integer, so they cannot collide with each other, which is what makes the loop
    /// terminate: every pass converts at least one still-plain token or there is nothing left to
    /// collide.
    /// </remarks>
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
        => IsBlank(featureName) ? IndexText(oneBasedIndex) : featureName!.Trim();

    private static bool IsBlank(string? featureName) => string.IsNullOrWhiteSpace(featureName);

    private static string IndexText(int oneBasedIndex)
        => oneBasedIndex.ToString(CultureInfo.InvariantCulture);
}
