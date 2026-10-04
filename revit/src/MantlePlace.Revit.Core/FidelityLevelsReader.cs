using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// Reads one entry's <c>levels</c> block (MPB 1.9.0) into its four levels, or <c>null</c> for an
/// entry that is MAX only.
/// </summary>
/// <remarks>
/// <para>
/// The schema's unknown-value rules, each applied where it says:
/// </para>
/// <list type="bullet">
/// <item>An absent block, or one that is not all four levels, is MAX only — what every bundle before
/// 1.9.0 is, and what an entry whose levels could not be stated in its own frame is.</item>
/// <item>A <c>same_as</c> naming a level this host does not know fails closed over the WHOLE block:
/// MAX only.</item>
/// <item>A cut of an unknown form, a level of no recognisable shape, and a <c>same_as</c> that chains
/// are that level alone, offered as unavailable and never as the whole file.</item>
/// <item>An unknown <c>same_as</c> reason is the same level without a reason.</item>
/// <item>An unknown cost unit keeps its count and loses its unit, so no cost is estimated from it.</item>
/// </list>
/// <para>
/// A pointer level states its own frame with the same keys as its entry, so it is read by the
/// entry's own shape (<paramref name="pointer"/>) and inherits no frame key from the entry: a level
/// that does not state its frame is refused by the planner as any such file is.
/// </para>
/// </remarks>
internal static class FidelityLevelsReader
{
    /// <param name="levels">The entry's <c>levels</c> member, or <c>null</c> when it has none.</param>
    /// <param name="pointer">Reads a pointer level as a file of the entry's kind; <c>null</c> for one with no path.</param>
    /// <param name="extent">Reads a pointer level's own ground extent, for an entry that publishes one.</param>
    internal static FidelityLevels? Read(
        JsonElement? levels,
        Func<JsonElement, BundleArtifact?> pointer,
        Func<JsonElement, GroundExtent?>? extent = null)
    {
        if (levels is not { ValueKind: JsonValueKind.Object } block)
        {
            return null;
        }

        Dictionary<FidelityLevel, JsonElement> members = [];
        foreach (FidelityLevel level in FidelityLevelNames.All)
        {
            if (block.Object(FidelityLevelNames.Token(level)) is not { } member)
            {
                return null;
            }

            members[level] = member;
        }

        // Every same_as target first: one this host does not know is the whole block's failure.
        foreach ((FidelityLevel level, JsonElement member) in members)
        {
            if (level != FidelityLevel.Max
                && member.TryGetProperty("same_as", out JsonElement target)
                && !(target.ValueKind == JsonValueKind.String && FidelityLevelNames.TryParse(target.GetString(), out _)))
            {
                return null;
            }
        }

        List<PublishedLevel> read = [.. FidelityLevelNames.All.Select(level => ReadLevel(level, members[level], pointer, extent))];

        // same_as never chains, and never names itself: a level that does is unavailable, rather than
        // followed to whatever the chain happens to end at.
        return new FidelityLevels(read.Select(level =>
            level is { Kind: FidelityLevelKind.SameAs, SameAs: { } target }
            && (target == level.Level || read.Single(other => other.Level == target).Kind == FidelityLevelKind.SameAs)
                ? Unavailable(level.Level, level.Cost)
                : level));
    }

    private static PublishedLevel ReadLevel(
        FidelityLevel level,
        JsonElement member,
        Func<JsonElement, BundleArtifact?> pointer,
        Func<JsonElement, GroundExtent?>? extent)
    {
        CostDriver? cost = ReadCost(member.Object("cost_driver"));

        // MAX is always the entry it sits in, so whatever else it carries names nothing.
        if (level == FidelityLevel.Max)
        {
            return new PublishedLevel { Level = level, Kind = FidelityLevelKind.Max, Cost = cost };
        }

        bool sameAs = member.TryGetProperty("same_as", out _);
        bool cut = member.TryGetProperty("cut", out _);
        bool path = member.TryGetProperty("path", out _);
        if ((sameAs ? 1 : 0) + (cut ? 1 : 0) + (path ? 1 : 0) != 1)
        {
            return Unavailable(level, cost);
        }

        if (sameAs)
        {
            FidelityLevelNames.TryParse(member.Str("same_as"), out FidelityLevel target);
            return new PublishedLevel
            {
                Level = level,
                Kind = FidelityLevelKind.SameAs,
                Cost = cost,
                SameAs = target,
                Reason = ReadReason(member.Str("reason")),
            };
        }

        if (cut)
        {
            // RAW is never a cut: it is the source-native data, and a cut is of the MAX file.
            return level == FidelityLevel.Raw ? Unavailable(level, cost) : ReadCut(level, member.Object("cut"), cost);
        }

        return pointer(member) is { } file
            ? new PublishedLevel
            {
                Level = level,
                Kind = FidelityLevelKind.Pointer,
                Cost = cost,
                File = file,
                Extent = extent?.Invoke(member),
            }
            : Unavailable(level, cost);
    }

    /// <summary>Exactly one known form; an unknown form, both, or neither is unavailable.</summary>
    private static PublishedLevel ReadCut(FidelityLevel level, JsonElement? cut, CostDriver? cost)
    {
        if (cut is not { } form)
        {
            return Unavailable(level, cost);
        }

        bool hasRows = form.TryGetProperty("rows", out JsonElement rows);
        bool hasField = form.TryGetProperty("lowest_level_field", out JsonElement field);

        if (hasRows && !hasField
            && rows.ValueKind == JsonValueKind.Number
            && rows.TryGetInt32(out int count)
            && count >= 0)
        {
            return new PublishedLevel { Level = level, Kind = FidelityLevelKind.RowCut, Cost = cost, Rows = count };
        }

        if (hasField && !hasRows
            && field.ValueKind == JsonValueKind.String
            && field.GetString() is { Length: > 0 } name)
        {
            return new PublishedLevel { Level = level, Kind = FidelityLevelKind.FieldCut, Cost = cost, LowestLevelField = name };
        }

        return Unavailable(level, cost);
    }

    private static PublishedLevel Unavailable(FidelityLevel level, CostDriver? cost)
        => new() { Level = level, Kind = FidelityLevelKind.Unavailable, Cost = cost };

    private static SameAsReason? ReadReason(string reason) => reason switch
    {
        "max_only" => SameAsReason.MaxOnly,
        "not_derived" => SameAsReason.NotDerived,
        "cap_not_reached" => SameAsReason.CapNotReached,
        "no_reduction" => SameAsReason.NoReduction,
        "smallest_valid_size" => SameAsReason.SmallestValidSize,
        _ => null,
    };

    /// <summary>The cost driver, or <c>null</c> when absent or without a whole, non-negative count.</summary>
    private static CostDriver? ReadCost(JsonElement? driver)
    {
        if (driver is not { } element
            || !element.TryGetProperty("count", out JsonElement count)
            || count.ValueKind != JsonValueKind.Number
            || !count.TryGetInt64(out long value)
            || value < 0)
        {
            return null;
        }

        string raw = element.Str("unit");
        CostUnit unit = raw switch
        {
            "elements" => CostUnit.Elements,
            "cuts" => CostUnit.Cuts,
            "triangles" => CostUnit.Triangles,
            "pixels" => CostUnit.Pixels,
            "posts" => CostUnit.Posts,
            _ => CostUnit.Unknown,
        };

        return new CostDriver(unit, raw, value, element.OptionalDouble("area_m2"));
    }
}
