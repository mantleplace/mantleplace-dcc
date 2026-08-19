namespace MantlePlace.Revit.Core;

/// <summary>
/// Turns a <c>dcc_readiness.revit.&lt;path&gt;.reason</c> token into a clause a human can read.
/// </summary>
/// <remarks>
/// <para>
/// <c>HPS-36</c> requires the manifest's own reason to be surfaced rather than replaced, and this
/// host used to satisfy that by interpolating the token straight into a sentence
/// (<c>$"No {label} in this bundle: {readiness.Reason}."</c>). Two vocabularies feed that field and
/// only one of them is closed: <c>packaging.not_delivered[].reason</c> has five members, while
/// <c>dcc_readiness</c> passes raw sidecar strings through, including <c>emit_threw:&lt;stage&gt;</c>
/// which is open-ended by construction. So a curator could be shown <c>emit_threw:mesh_stage_3</c>
/// in an import dialog as though it were an explanation.
/// </para>
/// <para>
/// Surfacing the reason and printing it verbatim are not the same thing. Every token the producer
/// can currently emit is mapped here; anything else still yields a clause, because "the bundle
/// explained itself and this plugin could not read the explanation" is information the generic
/// sentence would throw away — but the payload is quoted as a code when it is token-shaped and
/// dropped entirely when it is not.
/// </para>
/// <para>
/// Manifest v19 closes this vocabulary (the platform shipped <c>canonicalize_dcc_reason</c> as an
/// identity function; v19 flips it, collapses the six <c>*_not_produced</c> literals to
/// <c>not_produced</c> and adds <c>not_selected</c>). Those two tokens are deliberately NOT mapped
/// yet: the clean break (<c>HPS-31</c>) refuses a v19 bundle outright while the floor is 18, so an
/// alias for them would be unreachable code. Add them in the same change that raises the floor.
/// </para>
/// </remarks>
public static class ReadinessReasons
{
    /// <summary>An open-ended token: the suffix names an internal pipeline stage.</summary>
    private const string EmitThrewPrefix = "emit_threw:";

    /// <summary>Longest raw token this will quote back to a curator.</summary>
    private const int MaxQuotedTokenLength = 64;

    private static readonly Dictionary<string, string> KnownReasons = new(StringComparer.Ordinal)
    {
        // The closed set — mantleplace_terrain.loading.must_ship.NOT_DELIVERED_REASONS.
        ["no_features_in_aoi"] = "the source data has no features inside this area",
        ["emit_failed"] = "the platform could not produce it for this order",
        ["area_cap_exceeded"] = "this area is above the size cap for that deliverable",
        ["available_on_request"] =
            "it has not been produced for this order yet — pick it in your vault and it is generated at no extra cost",
        ["outside_coverage"] = "this area falls outside the source dataset's coverage",

        // A curator choice, not a delivery failure. "You did not order this" and "we could not make
        // it" are different sentences and both get printed.
        ["deselected_by_packaging_selection"] = "it was not part of what was ordered",

        // Six literals, one meaning. v19 collapses them to a single `not_produced`.
        ["points_csv_not_produced"] = NotProduced,
        ["surface_dxf_not_produced"] = NotProduced,
        ["ifc_site_not_produced"] = NotProduced,
        ["heightmap_not_produced"] = NotProduced,
        ["mesh_not_produced"] = NotProduced,
        ["cesium_terrain_not_produced"] = NotProduced,
    };

    private const string NotProduced = "the platform did not produce it for this order";

    /// <summary>
    /// The clause to append after "No {artifact} in this bundle: ", or <c>null</c> when the manifest
    /// stated no reason at all.
    /// </summary>
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
            // The stage name is an internal identifier. Matching the bounded prefix is what keeps
            // the unbounded suffix out of the dialog.
            return "an internal step failed while producing it";
        }

        return IsTokenShaped(token)
            ? $"the bundle gives a reason this plugin version does not recognise (\"{token}\")"
            : "the bundle gives a reason this plugin version cannot read";
    }

    /// <summary>
    /// Whether a value is short enough and plain enough to quote back verbatim.
    /// </summary>
    /// <remarks>
    /// Producer tokens are lowercase identifiers, optionally with a colon. Anything with a space, a
    /// control character, or four dozen characters of payload is a captured exception message rather
    /// than a token, and quoting it would put a paragraph — or a newline — into a Revit
    /// <c>TaskDialog</c>.
    /// </remarks>
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
