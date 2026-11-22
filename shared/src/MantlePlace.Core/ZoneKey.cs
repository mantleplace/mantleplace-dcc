namespace MantlePlace.Core;

public sealed record KeyRow(string Text, RegionStyle? Style);

public static class ZoneKey
{
    private const string Separator = " — ";

    public static string FloodRowText(string zone, string subtype)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(subtype);

        string named = zone.Length > 0 ? zone : "(no zone published)";
        return subtype.Length > 0 ? named + Separator + subtype : named;
    }

    public static string SteepRowText(string threshold)
        => string.IsNullOrEmpty(threshold)
            ? "Steep ground" + Separator + "threshold not stated"
            : "Steep ground" + Separator + "at or above " + threshold + "°";

    public static IReadOnlyList<KeyRow> FloodRows(IEnumerable<SiteFeature> drawn, FloodMap? map)
    {
        ArgumentNullException.ThrowIfNull(drawn);

        List<(string Zone, string Subtype)> seen = [];
        foreach (SiteFeature feature in drawn)
        {
            (string, string) pair = (feature.FloodZone, feature.FloodZoneSubtype);
            if (!feature.IsHole && !seen.Contains(pair))
            {
                seen.Add(pair);
            }
        }

        IReadOnlyList<string> order = map?.Zones ?? [];
        int Rank(string zone) => order.IndexOf(zone) is var index and >= 0 ? index : order.Count;

        return
        [
            .. seen
                .Select((pair, drawnAt) => (pair, drawnAt))
                .OrderBy(entry => Rank(entry.pair.Zone))
                .ThenBy(entry => entry.drawnAt)
                .Select(entry => new KeyRow(
                    FloodRowText(entry.pair.Zone, entry.pair.Subtype),
                    HazardStyles.ForFloodZone(entry.pair.Zone, entry.pair.Subtype))),
        ];
    }

    public static IReadOnlyList<string> FloodHeading(FloodMap? map)
    {
        List<string> lines =
        [
            map is { Source.Length: > 0 } ? $"Flood zones, as published by {map.Source}." : "Flood zones, as published.",
            "Context only" + Separator + "not a flood determination. Verify a zone against its FIRM panel.",
        ];

        if (map is { Panels.Count: > 0 })
        {
            lines.AddRange(map.Panels.Select(panel => panel.EffectiveDate is { } date
                ? $"FIRM panel {panel.Panel}, effective {date}"
                : $"FIRM panel {panel.Panel}, no effective date published"));
        }
        else if (map is { DfirmIds.Count: > 0 })
        {
            lines.Add($"Flood study {string.Join(", ", map.DfirmIds)}; no FIRM panel was published with this bundle.");
        }
        else
        {
            lines.Add("No FIRM panel was published with this bundle.");
        }

        return lines;
    }

    public static IReadOnlyList<KeyRow> SteepRows(IEnumerable<SiteFeature> drawn, string? manifestThreshold)
    {
        ArgumentNullException.ThrowIfNull(drawn);

        List<string> thresholds = [];
        foreach (SiteFeature feature in drawn)
        {
            string threshold = feature.Threshold.Length > 0 ? feature.Threshold : manifestThreshold ?? string.Empty;
            if (!feature.IsHole && !thresholds.Contains(threshold))
            {
                thresholds.Add(threshold);
            }
        }

        return [.. thresholds.Select(threshold => new KeyRow(SteepRowText(threshold), HazardStyles.SteepGround))];
    }

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (int index = 0; index < list.Count; index++)
        {
            if (string.Equals(list[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
