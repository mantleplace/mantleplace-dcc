namespace MantlePlace.Revit.Core;

/// <summary>
/// The words a renderer reads out of a material's name, keyed by the published <c>subtype</c> of the
/// polygon a subdivision was cut from. Pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a name.</b> Enscape grows 3D grass on any material whose name carries one of its grass
/// keywords, whatever that material's texture is, so a subdivision can keep the aerial photograph and
/// still grow grass: the photograph stays, and the keyword rides on the name. Nothing in the bundle
/// says "grass" to a renderer today, because every subdivision wears a drape material named for the
/// order alone.
/// </para>
/// <para>
/// <b>Host-owned.</b> This table is Revit rendering knowledge, like the smooth-shading anchor
/// (<see cref="DrapeAnchor"/>) — not a landscape binding and not a classification of the land. The
/// subtype is published; this only says which of a renderer's own words fits it, and a subtype the
/// table does not name gets no word at all rather than a guessed one.
/// </para>
/// <para>
/// ⛔ <b>Word order matters to the renderer, and the table is written in the renderer's order.</b>
/// Enscape's keywords are <c>grass</c>, <c>short grass</c>, <c>tall grass</c> and <c>wild grass</c>,
/// matched without regard to case but with regard to order: <c>tall grass</c> grows tall grass and
/// <c>grass tall</c> does not. So each entry is the renderer's phrase verbatim, adjective first, and
/// <see cref="GroundMaterialNames"/> appends it as the name's last words, where nothing the bundle
/// names can land between its words.
/// </para>
/// <para>
/// Water is deliberately absent from the subtype table. <c>land_cover</c>'s <c>wetland</c> is not
/// open water, and the <c>water</c> layer's own polygons take their word from the layer
/// (<see cref="ByLayer"/>) rather than from a subtype: a reservoir, a pond and a swimming pool are
/// all water.
/// </para>
/// </remarks>
public static class RendererKeywords
{
    /// <summary>The whole table, published subtype to renderer phrase.</summary>
    /// <remarks>
    /// Keyed on the subtype alone, not the layer, so a value either layer publishes means the same
    /// ground in both. Only the subtype is read: <c>land_use</c> publishes a <c>class</c> of
    /// <c>grass</c> under the subtype <c>managed</c>, and that polygon gets no keyword. Ordinal, because a
    /// subtype is a published value rather than a phrase to search — <c>Grass</c> and
    /// <c>grassland</c> are not <c>grass</c>, and are unknown.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Table { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // land_cover's grass; Enscape's medium, default grass.
        ["grass"] = "grass",

        // The forest floor under the canopy: taller, with more variation in the blades than a lawn.
        ["forest"] = "wild grass",

        // Scrub reads as tall growth, not as mown grass.
        ["shrub"] = "tall grass",

        // land_use: a park is mostly lawn.
        ["park"] = "grass",

        // land_use: a golf course is mostly fairway and green.
        ["golf"] = "short grass",
    };

    /// <summary>
    /// The word a whole layer's subdivisions carry, whatever each feature's subtype says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two layers publish one material between them. Every polygon in <c>water</c> is water — the
    /// subtypes under it (<c>reservoir</c>, <c>pond</c>, <c>human_made</c> for a swimming pool) are
    /// the kind of water body, not a different surface — and every polygon in <c>road_polygons</c> is
    /// a road surface, merged per class before it was published.
    /// </para>
    /// <para>
    /// ⛔ <b>These are plain material names first and renderer keywords second.</b> Enscape's
    /// documentation lists <c>water</c> among the words it reads out of a material name; nothing
    /// documents <c>asphalt</c> to any renderer, and it is here because it is what the surface is
    /// called. No renderer effect is claimed as verified for either — that is a licensed check a
    /// human makes.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<GroundLayer, string> ByLayer { get; } = new Dictionary<GroundLayer, string>
    {
        [GroundLayer.Water] = "water",
        [GroundLayer.RoadSurface] = "asphalt",
    };

    /// <summary>
    /// The phrase every subdivision of <paramref name="layer"/> carries, or <c>null</c> for a layer
    /// whose word comes from each feature's own subtype instead.
    /// </summary>
    public static string? ForLayer(GroundLayer layer)
        => ByLayer.TryGetValue(layer, out string? keyword) ? keyword : null;

    /// <summary>
    /// The renderer phrase for <paramref name="subtype"/>, or <c>null</c> — no keyword — for a
    /// subtype the table does not name, an empty one, or none.
    /// </summary>
    public static string? For(string? subtype)
        => subtype is not null && Table.TryGetValue(subtype, out string? keyword) ? keyword : null;

    /// <summary>The renderer phrase for the subdivision one published ring becomes.</summary>
    /// <remarks>
    /// A hole gets none. In the two land layers every ring is cut as its own subdivision, inner ones
    /// included, and an inner ring is where the polygon's subtype is <em>not</em> — the clearing in a
    /// forest — so naming it for the subtype would grow the forest's grass exactly where the bundle
    /// says the forest stops. The layers that keep their holes never ask: their word is the layer's
    /// (<see cref="ForLayer"/>) and a hole of theirs becomes no subdivision at all
    /// (<see cref="GroundCuts"/>).
    /// </remarks>
    public static string? ForRing(SiteFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return feature.IsHole ? null : For(feature.Subtype);
    }
}
