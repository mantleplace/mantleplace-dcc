using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct RgbColour(byte Red, byte Green, byte Blue)
{
    public override string ToString()
        => string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", Red, Green, Blue);
}

public sealed record RegionStyle(
    string TypeName,
    RgbColour? Fill,
    RgbColour? Hatch,
    double HatchAngleDeg,
    string HatchPatternName);

public static class HazardStyles
{
    public static readonly RgbColour Neutral = new(190, 190, 190);

    private const string TypePrefix = "Mantle Place ";

    private const string FloodwaySubtype = "FLOODWAY";

    private static readonly RgbColour FloodwayHatch = new(200, 40, 40);

    private static readonly RgbColour SteepHatch = new(140, 70, 20);

    private static readonly RgbColour OnePercent = new(116, 173, 237);

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

        [("X", "1 PCT FUTURE CONDITIONS")] = new(196, 164, 222),
        [("X", "AREA OF MINIMAL FLOOD HAZARD")] = new(228, 228, 216),
    };

    public static RegionStyle SteepGround { get; } = new(
        TypePrefix + "Steep Ground",
        Fill: null,
        Hatch: SteepHatch,
        HatchAngleDeg: 135.0,
        HatchPatternName: TypePrefix + "Steep Ground Hatch");

    public static RegionStyle ForFloodZone(string zone, string subtype)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(subtype);

        RgbColour fill = ByZoneAndSubtype.TryGetValue((zone, subtype), out RgbColour exact) ? exact
            : ByZone.TryGetValue(zone, out RgbColour byZone) ? byZone
            : Neutral;

        bool floodway = subtype.Contains(FloodwaySubtype, StringComparison.Ordinal);
        return new RegionStyle(
            LegalName(TypePrefix + "Flood Zone " + ZoneKey.FloodRowText(zone, subtype)),
            fill,
            floodway ? FloodwayHatch : null,
            HatchAngleDeg: 45.0,
            HatchPatternName: TypePrefix + "Floodway Hatch");
    }

    internal static string LegalName(string name)
    {
        char[] refused = ['{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\'];
        string legal = new([.. name.Select(character => Array.IndexOf(refused, character) >= 0 ? '-' : character)]);
        return legal.Trim();
    }
}
