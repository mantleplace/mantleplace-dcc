using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>An opaque colour, as Revit's <c>Color</c> takes it.</summary>
public readonly record struct RgbColour(byte Red, byte Green, byte Blue)
{
    public override string ToString()
        => string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", Red, Green, Blue);
}

/// <summary>
/// How one kind of hazard region is drawn: the filled region type it is given, its fill, and the
/// hatch laid over that fill.
/// </summary>
/// <param name="TypeName">
/// The filled region type's name, which is also its identity: a type already in the project under
/// this name is reused, so a curator who recoloured it keeps their colour.
/// </param>
/// <param name="Fill">The solid fill, or <c>null</c> for none — steep ground shows the flood colour through it.</param>
/// <param name="Hatch">The hatch's colour, or <c>null</c> for no hatch.</param>
/// <param name="HatchAngleDeg">The hatch's angle from the view's horizontal, in degrees.</param>
/// <param name="HatchPatternName">The drafting fill pattern the hatch is, found or made by this name.</param>
public sealed record HazardStyle(
    string TypeName,
    RgbColour? Fill,
    RgbColour? Hatch,
    double HatchAngleDeg,
    string HatchPatternName);

/// <summary>
/// The hazard plan's colours, owned by this host and keyed on the published words. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Colour is presentation, not derivation: the zone is the flood map's, and this table only says how
/// this host draws it. It is keyed on (<c>fld_zone</c>, <c>zone_subty</c>), falling back to
/// <c>fld_zone</c> alone for a subtype it does not know, and to <see cref="Neutral"/> for a zone it does
/// not know. Nothing is ever dropped and nothing is guessed: an unknown zone is drawn, in a type named
/// with its published code.
/// </para>
/// <para>
/// The colours follow the convention a planner has seen on a flood map — the special flood hazard
/// area in blue, the coastal high-hazard zone darker, the 0.2 % chance area in orange, minimal hazard
/// pale — so the plan reads without the key, and the key says exactly what each one is.
/// </para>
/// </remarks>
public static class HazardStyles
{
    /// <summary>What a zone this table does not know is drawn in.</summary>
    public static readonly RgbColour Neutral = new(190, 190, 190);

    private const string TypePrefix = "Mantle Place ";

    private const string FloodwaySubtype = "FLOODWAY";

    private static readonly RgbColour FloodwayHatch = new(200, 40, 40);

    private static readonly RgbColour SteepHatch = new(140, 70, 20);

    /// <summary>The special flood hazard area, the 1 % annual chance flood.</summary>
    private static readonly RgbColour OnePercent = new(116, 173, 237);

    /// <summary>The coastal high-hazard area.</summary>
    private static readonly RgbColour Coastal = new(52, 101, 164);

    private static readonly Dictionary<string, RgbColour> ByZone = new(StringComparer.Ordinal)
    {
        ["A"] = OnePercent,
        ["AE"] = OnePercent,
        ["AH"] = OnePercent,
        ["AO"] = OnePercent,
        ["AR"] = OnePercent,
        ["A99"] = OnePercent,
        ["V"] = Coastal,
        ["VE"] = Coastal,
        ["X"] = new(228, 228, 216),
        ["D"] = new(170, 170, 170),
        ["OPEN WATER"] = new(150, 200, 235),
    };

    private static readonly Dictionary<(string Zone, string Subtype), RgbColour> ByZoneAndSubtype = new()
    {
        [("X", "0.2 PCT ANNUAL CHANCE FLOOD HAZARD")] = new(247, 186, 108),

        // A 1 % chance flood under future conditions is a real hazard, and drawn as pale as minimal
        // hazard it would read as none.
        [("X", "1 PCT FUTURE CONDITIONS")] = new(196, 164, 222),
        [("X", "AREA OF MINIMAL FLOOD HAZARD")] = new(228, 228, 216),
    };

    /// <summary>
    /// Steep ground: a hatch and no fill, drawn after the flood zones so a steep bank inside a flood
    /// zone shows both — at an angle and in a colour the floodway's hatch does not share.
    /// </summary>
    public static HazardStyle SteepGround { get; } = new(
        TypePrefix + "Steep Ground",
        Fill: null,
        Hatch: SteepHatch,
        HatchAngleDeg: 135.0,
        HatchPatternName: TypePrefix + "Steep Ground Hatch");

    /// <summary>How a flood zone of this published zone and subtype is drawn.</summary>
    public static HazardStyle ForFloodZone(string zone, string subtype)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(subtype);

        RgbColour fill = ByZoneAndSubtype.TryGetValue((zone, subtype), out RgbColour exact) ? exact
            : ByZone.TryGetValue(zone, out RgbColour byZone) ? byZone
            : Neutral;

        // Every floodway FEMA names — FLOODWAY, ADMINISTRATIVE FLOODWAY and their like — is hatched.
        bool floodway = subtype.Contains(FloodwaySubtype, StringComparison.Ordinal);
        return new HazardStyle(
            LegalName(TypePrefix + "Flood Zone " + ZoneKey.FloodRowText(zone, subtype)),
            fill,
            floodway ? FloodwayHatch : null,
            HatchAngleDeg: 45.0,
            HatchPatternName: TypePrefix + "Floodway Hatch");
    }

    /// <summary>
    /// A name Revit accepts for a type: the characters it refuses in an element name become a hyphen.
    /// </summary>
    /// <remarks>
    /// The type's name only. The zone key prints the published words untouched; the type name is an
    /// identity in the project browser, and a zone whose code happened to carry a colon must still
    /// get one.
    /// </remarks>
    internal static string LegalName(string name)
    {
        char[] refused = ['{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\'];
        string legal = new([.. name.Select(character => Array.IndexOf(refused, character) >= 0 ? '-' : character)]);
        return legal.Trim();
    }
}
