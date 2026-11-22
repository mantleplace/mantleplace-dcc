using System.Globalization;

namespace MantlePlace.Core;

public static class DeliveryHeader
{
    public const string Separator = " · ";

    public const string NoDeliveryCrs = "no delivery CRS";

    public const string NotStated = "not stated";

    public static string? UnitSystemWord(UnitSystem system) => system switch
    {
        UnitSystem.Metric => "Metric",
        UnitSystem.Imperial => "Imperial",
        _ => null,
    };

    public static string? LinearUnitWord(LinearUnit unit) => unit switch
    {
        LinearUnit.Metre => "metres",
        LinearUnit.UsSurveyFoot => "US survey feet",
        LinearUnit.InternationalFoot => "international feet",
        _ => null,
    };

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

    public static UnitSystem? DisplaySystem(string lengthUnitTypeId)
    {
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
