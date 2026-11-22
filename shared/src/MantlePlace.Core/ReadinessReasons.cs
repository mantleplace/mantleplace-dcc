namespace MantlePlace.Core;

public static class ReadinessReasons
{
    private const string EmitThrewPrefix = "emit_threw:";

    private const int MaxQuotedTokenLength = 64;

    private static readonly Dictionary<string, string> KnownReasons = new(StringComparer.Ordinal)
    {
        ["no_features_in_aoi"] = "the source data has no features inside this area",
        ["emit_failed"] = "the platform could not produce it for this order",
        ["area_cap_exceeded"] = "this area is above the size cap for that deliverable",
        ["available_on_request"] =
            "it has not been produced for this order yet — pick it in your vault and it is generated at no extra cost",
        ["outside_coverage"] = "this area falls outside the source dataset's coverage",

        ["deselected_by_packaging_selection"] = "it was not part of what was ordered",

        ["points_csv_not_produced"] = NotProduced,
        ["surface_dxf_not_produced"] = NotProduced,
        ["ifc_site_not_produced"] = NotProduced,
        ["heightmap_not_produced"] = NotProduced,
        ["mesh_not_produced"] = NotProduced,
        ["cesium_terrain_not_produced"] = NotProduced,

        ["not_produced"] = NotProduced,
        ["not_selected"] = "it was not part of what was ordered",
    };

    private const string NotProduced = "the platform did not produce it for this order";

    public static string? ClauseFor(string? rawReason)
    {
        if (string.IsNullOrWhiteSpace(rawReason))
        {
            return null;
        }

        string token = rawReason.Trim();

        if (KnownReasons.TryGetValue(token, out string? known))
        {
            return known;
        }

        if (token.StartsWith(EmitThrewPrefix, StringComparison.Ordinal))
        {
            return "an internal step failed while producing it";
        }

        return IsTokenShaped(token)
            ? $"the bundle gives a reason this plugin version does not recognise (\"{token}\")"
            : "the bundle gives a reason this plugin version cannot read";
    }

    private static bool IsTokenShaped(string token)
    {
        if (token.Length > MaxQuotedTokenLength)
        {
            return false;
        }

        foreach (char character in token)
        {
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
