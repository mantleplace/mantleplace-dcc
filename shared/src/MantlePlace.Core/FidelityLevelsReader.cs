using System.Text.Json;

namespace MantlePlace.Core;

internal static class FidelityLevelsReader
{
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

        return new CostDriver(unit, raw, value);
    }
}
