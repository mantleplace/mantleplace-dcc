using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One site-boundary feature the current import still has to create.</summary>
/// <param name="Ordinal">The feature's ONE-based position in the layer, the same number the stamp carries.</param>
/// <param name="Stamp">The identity written to the subdivision's Comments parameter.</param>
public readonly record struct NewSiteBoundary(int Ordinal, string Stamp);

/// <summary>
/// Decides which site-boundary features an import still has to create, and what identity each new
/// subdivision is stamped with.
/// </summary>
/// <remarks>
/// <para>
/// A subdivision has no name slot, so the <c>Material</c>/<c>ToposolidType</c> precedent —
/// name-from-cache-key, reuse when found — lands in its instance Comments parameter instead. The
/// stamp is <c>Mantle Place Site Boundary {stem}/{feature}</c>: the stem scopes it to one bundle, so
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
    private const string Prefix = "Mantle Place Site Boundary ";

    /// <summary>The stamp for one feature, before any list-level disambiguation.</summary>
    /// <remarks>
    /// A null or blank name falls back to the one-based position. Duplicate names are the caller's
    /// list-level problem and are resolved by <see cref="NewFeatures"/>, which suffixes the position
    /// — one feature alone cannot know its name is shared.
    /// </remarks>
    public static string Stamp(string cacheKeyStem, string? featureName, int oneBasedIndex)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return Prefix + cacheKeyStem + "/" + TokenOf(featureName, oneBasedIndex);
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
        IReadOnlyCollection<string> existingStamps,
        IReadOnlyList<string?> featureNames,
        string cacheKeyStem)
    {
        ArgumentNullException.ThrowIfNull(existingStamps);
        ArgumentNullException.ThrowIfNull(featureNames);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        HashSet<string> existing = new(existingStamps, StringComparer.Ordinal);
        IReadOnlyList<string> tokens = DistinctTokens(featureNames);

        List<NewSiteBoundary> created = [];
        for (int index = 0; index < tokens.Count; index++)
        {
            string stamp = Prefix + cacheKeyStem + "/" + tokens[index];
            if (!existing.Contains(stamp))
            {
                created.Add(new NewSiteBoundary(index + 1, stamp));
            }
        }

        return created;
    }

    /// <summary>
    /// Whether a subdivision's Comments string is a stamp THIS plugin wrote for THIS bundle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drape needs this because it cannot use <c>ChangeTypeId</c>: a subdivision is a typeless
    /// element, so the material goes on the instance, and the instance has to be found. Finding it by
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
    /// <summary>
    /// The per-feature token of a stamp this import owns — the part after the stem — or <c>null</c>
    /// for any other Comments. What names a subdivision's own drape material, so a re-import finds
    /// the material it made rather than growing another.
    /// </summary>
    public static string? Token(string? comments, string cacheKeyStem)
    {
        if (!IsStampFor(comments, cacheKeyStem))
        {
            return null;
        }

        return comments![(Prefix + cacheKeyStem + "/").Length..];
    }

    public static bool IsStampFor(string? comments, string cacheKeyStem)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        // Ordinal over the whole prefix INCLUDING the separator. Without the trailing "/", stem
        // "abc" would claim stem "abcdef"'s subdivisions — and cache-key stems are truncated
        // hashes, where one being a prefix of another is a collision waiting rather than a
        // hypothetical.
        string prefix = Prefix + cacheKeyStem + "/";
        return comments is not null
            && comments.StartsWith(prefix, StringComparison.Ordinal)
            && comments.Length > prefix.Length;
    }

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
