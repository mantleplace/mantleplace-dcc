using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What the import window says about the order's <c>delivery</c> block before any step runs: its unit
/// system, its linear unit and its delivery CRS on one line, and a second line only when the project
/// displays lengths in the other unit system.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read verbatim, never looked up.</b> The delivery CRS prints as <c>delivery.label</c> where the
/// bundle carries one (MPB 1.3.0), exactly as published. A bundle built before the label existed
/// prints its EPSG code instead: the format says the same words follow from <c>tier</c> and
/// <c>horizontal_epsg</c>, but deriving them would need a CRS table in this repository, and a table
/// here is a second authority on something the format owns.
/// </para>
/// <para>
/// <b>Unknown is shown, not guessed.</b> A unit system this reader does not know is printed as
/// published, and a required value the block leaves out reads <see cref="NotStated"/>. An unknown
/// linear unit never reaches here: it refuses the manifest (<c>HPS-35</c>), because scale depends on
/// it. A block that names neither a label nor an EPSG code reads <see cref="NoDeliveryCrs"/> — the
/// <c>local_ft</c> tier before the label existed, where the site origin's UTM code is not the files'
/// CRS. A bundle with no <c>delivery</c> block — built before the block existed — gets no line at
/// all, which is not the same as a metric line.
/// </para>
/// <para>
/// <b>The words are the standard's</b> (<c>HPS-51</c>): the reference host will say a bundle's unit
/// system and linear unit too, and says them with these words. The casing is this host's.
/// </para>
/// </remarks>
public static class DeliveryHeader
{
    /// <summary>Between the line's parts.</summary>
    public const string Separator = " · ";

    /// <summary>The delivery CRS of a block that publishes neither a label nor an EPSG code.</summary>
    public const string NoDeliveryCrs = "no delivery CRS";

    /// <summary>A required value the block leaves out.</summary>
    public const string NotStated = "not stated";

    /// <summary>A known unit system's word; an unknown one is shown as published instead.</summary>
    public static string? UnitSystemWord(UnitSystem system) => system switch
    {
        UnitSystem.Metric => "Metric",
        UnitSystem.Imperial => "Imperial",
        _ => null,
    };

    /// <summary>A known linear unit's word.</summary>
    public static string? LinearUnitWord(LinearUnit unit) => unit switch
    {
        LinearUnit.Metre => "metres",
        LinearUnit.UsSurveyFoot => "US survey feet",
        LinearUnit.InternationalFoot => "international feet",
        _ => null,
    };

    /// <summary>
    /// The line — <c>Imperial · US survey feet · EPSG:6543</c> — or <c>null</c> when the bundle
    /// publishes no <c>delivery</c> block. Always three parts, in that order.
    /// </summary>
    public static string? Describe(DeliveryFacts delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (!delivery.Declared)
        {
            return null;
        }

        string unitSystem = UnitSystemWord(delivery.UnitSystem)
            ?? (string.IsNullOrWhiteSpace(delivery.UnitSystemValue) ? NotStated : delivery.UnitSystemValue);

        return string.Join(Separator, unitSystem, LinearUnitWord(delivery.LinearUnit) ?? NotStated, DeliveryCrs(delivery));
    }

    /// <summary>
    /// The line that says the project displays lengths in the other unit system, or <c>null</c> when
    /// they agree or either side is unknown. It changes nothing: display units are the curator's.
    /// </summary>
    /// <param name="lengthUnitTypeId">
    /// The <c>TypeId</c> of the unit the project formats lengths in, as Revit reports it —
    /// <c>autodesk.unit.unit:meters-1.0.0</c>. A string rather than a Revit type, so this assembly
    /// stays free of the Revit API.
    /// </param>
    public static string? DisplayDisagreement(DeliveryFacts delivery, string lengthUnitTypeId)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (!delivery.Declared || delivery.UnitSystem == UnitSystem.Unspecified)
        {
            return null;
        }

        if (DisplaySystem(lengthUnitTypeId) is not { } project || project == delivery.UnitSystem)
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "This project displays lengths in {0} units and this order is {1}. The import leaves Project Units as they are.",
            Lower(project),
            Lower(delivery.UnitSystem));
    }

    /// <summary>
    /// Which unit system a Revit length unit belongs to, from its <c>TypeId</c>; <c>null</c> for a
    /// unit this list does not name, so a unit Revit adds later shows no line rather than a wrong one.
    /// </summary>
    /// <remarks>
    /// The names are the length quantity's own units as Revit 2025 and 2027 ship them, plus Revit's
    /// combined feet-and-inches and metres-and-centimetres forms. Nautical miles and the shaku belong
    /// to neither system and show no line. Only the name is read, never the namespace or the version,
    /// so a unit revised from <c>-1.0.0</c> to <c>-1.0.1</c> is still the same unit.
    /// </remarks>
    public static UnitSystem? DisplaySystem(string lengthUnitTypeId)
    {
        // autodesk.unit.unit:<name>-<version>: the name is between the last colon and the dash.
        string id = lengthUnitTypeId ?? string.Empty;
        int colon = id.LastIndexOf(':');
        string name = colon < 0 ? id : id[(colon + 1)..];
        int dash = name.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            name = name[..dash];
        }

        return name switch
        {
            "meters" or "centimeters" or "millimeters" or "decimeters" or "hectometers" or "kilometers"
                or "microns" or "nanometers" or "metersCentimeters" => UnitSystem.Metric,
            "feet" or "inches" or "feetFractionalInches" or "fractionalInches" or "usSurveyFeet"
                or "yards" or "miles" or "mils" or "microinches" => UnitSystem.Imperial,
            _ => null,
        };
    }

    private static string DeliveryCrs(DeliveryFacts delivery)
    {
        if (delivery.Label is { } label)
        {
            return label;
        }

        return delivery.HorizontalEpsg is { } epsg
            ? string.Create(CultureInfo.InvariantCulture, $"EPSG:{epsg}")
            : NoDeliveryCrs;
    }

    private static string Lower(UnitSystem system)
        => UnitSystemWord(system)?.ToLowerInvariant() ?? throw new ArgumentOutOfRangeException(nameof(system));
}
