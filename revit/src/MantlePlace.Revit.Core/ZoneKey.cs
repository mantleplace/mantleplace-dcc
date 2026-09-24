namespace MantlePlace.Revit.Core;

/// <summary>One line of a zone key: its text, and the swatch beside it, or none for a heading.</summary>
public sealed record ZoneKeyRow(string Text, HazardStyle? Style);

/// <summary>
/// What the zone key inside a hazard plan says: a row for each zone and threshold the plan shows, in
/// the published words, under a heading that says the plan is context. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Only what was drawn is listed. A zone the flood map names and this plan does not show — a hole's
/// subtype, or a zone clipped out of the area — would be a row explaining nothing on the page.
/// </para>
/// <para>
/// Every row is the manifest's own text. A zone is its <c>fld_zone</c> and <c>zone_subty</c> as
/// published; a threshold is the number as written, in degrees, with no comparison word: the bundle
/// does not say whether steep means above or at-or-above, so the key claims neither.
/// </para>
/// </remarks>
public static class ZoneKey
{
    private const string Separator = " — ";

    /// <summary>A flood zone's row: the zone, and its subtype after a dash when it has one.</summary>
    public static string FloodRowText(string zone, string subtype)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(subtype);

        string named = zone.Length > 0 ? zone : "(no zone published)";
        return subtype.Length > 0 ? named + Separator + subtype : named;
    }

    /// <summary>Steep ground's row, for a threshold as the manifest wrote it.</summary>
    public static string SteepRowText(string threshold)
        => string.IsNullOrEmpty(threshold)
            ? "Steep ground" + Separator + "threshold not stated"
            : "Steep ground" + Separator + "threshold " + threshold + "°";

    /// <summary>
    /// The rows for the flood zones drawn: each (zone, subtype) once, in the order the flood map lists
    /// its zones, then in the order they were drawn.
    /// </summary>
    /// <param name="drawn">The rings drawn, holes included; a hole claims nothing.</param>
    /// <param name="map">The flood map's facts, for its zone order, or <c>null</c>.</param>
    public static IReadOnlyList<ZoneKeyRow> FloodRows(IEnumerable<SiteFeature> drawn, FloodMap? map)
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
                .Select(entry => new ZoneKeyRow(
                    FloodRowText(entry.pair.Zone, entry.pair.Subtype),
                    HazardStyles.ForFloodZone(entry.pair.Zone, entry.pair.Subtype))),
        ];
    }

    /// <summary>
    /// The lines above the flood rows: whose map it is, that it is context and not a determination,
    /// and the panels to verify a zone against.
    /// </summary>
    /// <remarks>
    /// A panel the platform could not name is not guessed at: without panels the study ids are said,
    /// and without either, that none was published.
    /// </remarks>
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

    /// <summary>
    /// The rows for the steep ground drawn: one per threshold its features state, in the order drawn.
    /// </summary>
    /// <param name="drawn">The rings drawn.</param>
    /// <param name="manifestThreshold">
    /// <c>elevation.steep_slope.threshold_deg</c> as written, for a feature that states none.
    /// </param>
    public static IReadOnlyList<ZoneKeyRow> SteepRows(IEnumerable<SiteFeature> drawn, string? manifestThreshold)
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

        return [.. thresholds.Select(threshold => new ZoneKeyRow(SteepRowText(threshold), HazardStyles.SteepGround))];
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
