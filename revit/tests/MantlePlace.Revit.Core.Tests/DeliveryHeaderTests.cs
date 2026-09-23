namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The import window's line about the order's unit system, linear unit and delivery CRS, and the
/// line that appears only when the project displays lengths in the other unit system.
/// </summary>
/// <remarks>
/// Driven through <see cref="BundleManifestReader.Parse"/> rather than a hand-built
/// <see cref="DeliveryFacts"/>, because a published value the reader drops is a line that says the
/// wrong thing, and only the reader's path can show that it was kept.
/// </remarks>
internal static class DeliveryHeaderTests
{
    private const string Meters = "autodesk.unit.unit:meters-1.0.0";
    private const string Millimeters = "autodesk.unit.unit:millimeters-1.0.1";
    private const string Feet = "autodesk.unit.unit:feet-1.0.1";
    private const string FeetFractionalInches = "autodesk.unit.unit:feetFractionalInches-1.0.0";
    private const string UsSurveyFeet = "autodesk.unit.unit:usSurveyFeet-1.0.0";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a metric order reads its words and its EPSG code", () =>
        {
            DeliveryFacts delivery = Read("""{"unit_system": "metric", "tier": "metric", "linear_unit": "m", "horizontal_epsg": 32613}""");
            run.Equal(DeliveryHeader.Describe(delivery), "Metric · metres · EPSG:32613", "the metric line");
        });

        run.Case("an imperial order on a State Plane tier reads its words and its EPSG code", () =>
        {
            DeliveryFacts delivery = Read("""{"unit_system": "imperial", "tier": "sp_ftus", "linear_unit": "ftUS", "horizontal_epsg": 6543}""");
            run.Equal(DeliveryHeader.Describe(delivery), "Imperial · US survey feet · EPSG:6543", "the example the line was specified with");
        });

        run.Case("the international foot is not the US survey foot", () =>
        {
            DeliveryFacts delivery = Read("""{"unit_system": "imperial", "tier": "sp_ft", "linear_unit": "ft", "horizontal_epsg": 2223}""");
            run.Equal(DeliveryHeader.Describe(delivery), "Imperial · international feet · EPSG:2223", "the sp_ft line");
        });

        run.Case("the local-feet tier before the label names no delivery CRS, and the line says so", () =>
        {
            // The schema publishes horizontal_epsg as null here: the vectors live in a local frame.
            // local_origin carries a UTM EPSG, but that is the origin's CRS, not the delivery's, and
            // printing it would say the files are on a grid they are not on.
            DeliveryFacts delivery = Read("""
                {"unit_system": "imperial", "tier": "local_ft", "linear_unit": "ft", "horizontal_epsg": null,
                 "local_origin": {"lon": -155.5, "lat": 19.6, "utm_epsg": 32605, "easting_m": 1, "northing_m": 2}}
                """);
            run.Equal(DeliveryHeader.Describe(delivery), "Imperial · international feet · no delivery CRS", "the local_ft line");
        });

        run.Case("a published delivery label is printed verbatim in place of the EPSG code (MPB 1.3.0)", () =>
        {
            DeliveryFacts delivery = Read("""
                {"unit_system": "imperial", "tier": "sp_ftus", "linear_unit": "ftUS", "horizontal_epsg": 6543,
                 "label": "NAD83(2011) / Colorado Central (ftUS)"}
                """);
            run.Equal(
                DeliveryHeader.Describe(delivery),
                "Imperial · US survey feet · NAD83(2011) / Colorado Central (ftUS)",
                "the label, as published");
        });

        run.Case("on the local-feet tier the published label wins over the host's own words", () =>
        {
            DeliveryFacts delivery = Read("""
                {"unit_system": "imperial", "tier": "local_ft", "linear_unit": "ft", "horizontal_epsg": null,
                 "label": "Local site grid (ft)"}
                """);
            run.Equal(DeliveryHeader.Describe(delivery), "Imperial · international feet · Local site grid (ft)", "the label, as published");
        });

        run.Case("a blank label is no label, and the EPSG code stands", () =>
        {
            DeliveryFacts delivery = Read("""{"unit_system": "metric", "tier": "metric", "linear_unit": "m", "horizontal_epsg": 32613, "label": "  "}""");
            run.Equal(DeliveryHeader.Describe(delivery), "Metric · metres · EPSG:32613", "the fallback");
        });

        run.Case("a bundle with no delivery block shows no line rather than a guess", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse("""{"version": "1.0.0", "hosts": {"unreal": {}}}""");
            run.True(manifest.IsValid, $"accepted ({manifest.Error})");
            run.True(DeliveryHeader.Describe(manifest.Delivery) is null, "no line");
        });

        run.Case("an unknown unit system is shown as published, never guessed at", () =>
        {
            DeliveryFacts delivery = Read("""{"unit_system": "nautical", "tier": "metric", "linear_unit": "m", "horizontal_epsg": 32613}""");
            run.Equal(DeliveryHeader.Describe(delivery), "nautical · metres · EPSG:32613", "the published value");
        });

        run.Case("a block naming neither a label nor an EPSG code says so rather than naming one", () =>
        {
            // Not the tier, which is not a CRS, and never a name looked up from it.
            DeliveryFacts delivery = Read("""{"unit_system": "imperial", "tier": "county_ft", "linear_unit": "ft"}""");
            run.Equal(DeliveryHeader.Describe(delivery), "Imperial · international feet · no delivery CRS", "no CRS named");
        });

        run.Case("a required value the block leaves out reads not stated", () =>
        {
            run.Equal(
                DeliveryHeader.Describe(Read("""{"tier": "metric", "linear_unit": "m", "horizontal_epsg": 32613}""")),
                "not stated · metres · EPSG:32613",
                "no unit_system");
            run.Equal(
                DeliveryHeader.Describe(Read("""{"unit_system": "metric", "tier": "metric", "horizontal_epsg": 32613}""")),
                "Metric · not stated · EPSG:32613",
                "no linear_unit");
        });

        run.Case("a 1.3.0 block without the label keeps the EPSG code", () =>
        {
            DeliveryFacts delivery = Read("""{"unit_system": "imperial", "tier": "sp_ft", "linear_unit": "ft", "horizontal_epsg": 2223}""");
            run.True(delivery.Label is null, "no label read");
            run.Equal(DeliveryHeader.Describe(delivery), "Imperial · international feet · EPSG:2223", "the fallback");
        });

        run.Case("an unknown linear unit still refuses the manifest, so no line can print it (HPS-35)", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                """{"version": "1.0.0", "hosts": {"unreal": {}}, "delivery": {"unit_system": "imperial", "tier": "sp_ft", "linear_unit": "furlong"}}""");
            run.False(manifest.IsValid, "refused, as before");
        });

        run.Case("the words are the standard's HPS-51 table", () =>
        {
            run.Equal(DeliveryHeader.UnitSystemWord(UnitSystem.Metric), "Metric", "metric");
            run.Equal(DeliveryHeader.UnitSystemWord(UnitSystem.Imperial), "Imperial", "imperial");
            run.Equal(DeliveryHeader.LinearUnitWord(LinearUnit.Metre), "metres", "m");
            run.Equal(DeliveryHeader.LinearUnitWord(LinearUnit.UsSurveyFoot), "US survey feet", "ftUS");
            run.Equal(DeliveryHeader.LinearUnitWord(LinearUnit.InternationalFoot), "international feet", "ft");
            run.Equal(DeliveryHeader.NoDeliveryCrs, "no delivery CRS", "neither a label nor an EPSG code");
            run.Equal(DeliveryHeader.NotStated, "not stated", "a required value absent");
        });

        run.Case("a metric-display project opening an imperial order says so", () =>
        {
            DeliveryFacts imperial = Read("""{"unit_system": "imperial", "tier": "sp_ftus", "linear_unit": "ftUS", "horizontal_epsg": 6543}""");
            foreach (string unit in new[] { Meters, Millimeters })
            {
                run.Equal(
                    DeliveryHeader.DisplayDisagreement(imperial, unit),
                    "This project displays lengths in metric units and this order is imperial. The import leaves Project Units as they are.",
                    $"the project's system first, the order's second, for {unit}");
            }
        });

        run.Case("an imperial-display project opening a metric order says so", () =>
        {
            DeliveryFacts metric = Read("""{"unit_system": "metric", "tier": "metric", "linear_unit": "m", "horizontal_epsg": 32613}""");
            foreach (string unit in new[] { Feet, FeetFractionalInches, UsSurveyFeet })
            {
                run.Equal(
                    DeliveryHeader.DisplayDisagreement(metric, unit),
                    "This project displays lengths in imperial units and this order is metric. The import leaves Project Units as they are.",
                    $"the project's system first, the order's second, for {unit}");
            }
        });

        run.Case("only the unit's name is read, not its namespace or version", () =>
        {
            run.True(DeliveryHeader.DisplaySystem("autodesk.unit.unit:meters-1.0.1") == UnitSystem.Metric, "a revised version");
            run.True(DeliveryHeader.DisplaySystem("autodesk.revit.unit:feetFractionalInches-1.0.0") == UnitSystem.Imperial, "another namespace");
            run.True(DeliveryHeader.DisplaySystem("autodesk.unit.unit:kilometers-1.0.1") == UnitSystem.Metric, "kilometres");
            run.True(DeliveryHeader.DisplaySystem("autodesk.unit.unit:yards-1.0.1") == UnitSystem.Imperial, "yards");
        });

        run.Case("agreement shows no line", () =>
        {
            DeliveryFacts metric = Read("""{"unit_system": "metric", "tier": "metric", "linear_unit": "m", "horizontal_epsg": 32613}""");
            DeliveryFacts imperial = Read("""{"unit_system": "imperial", "tier": "sp_ftus", "linear_unit": "ftUS", "horizontal_epsg": 6543}""");
            run.True(DeliveryHeader.DisplayDisagreement(metric, Meters) is null, "metric in metric");
            run.True(DeliveryHeader.DisplayDisagreement(metric, Millimeters) is null, "millimetres are metric");
            run.True(DeliveryHeader.DisplayDisagreement(imperial, FeetFractionalInches) is null, "imperial in imperial");
        });

        run.Case("anything unknown on either side shows no line", () =>
        {
            DeliveryFacts imperial = Read("""{"unit_system": "imperial", "tier": "sp_ftus", "linear_unit": "ftUS", "horizontal_epsg": 6543}""");
            DeliveryFacts nautical = Read("""{"unit_system": "nautical", "tier": "metric", "linear_unit": "m"}""");
            BundleManifest undeclared = BundleManifestReader.Parse("""{"version": "1.0.0", "hosts": {"unreal": {}}}""");

            run.True(DeliveryHeader.DisplayDisagreement(imperial, "autodesk.unit.unit:furlongs-1.0.0") is null, "a unit Revit adds later");
            run.True(DeliveryHeader.DisplayDisagreement(imperial, "autodesk.unit.unit:nauticalMiles-1.0.0") is null, "a unit of neither system");
            run.True(DeliveryHeader.DisplayDisagreement(imperial, string.Empty) is null, "no unit read");
            run.True(DeliveryHeader.DisplayDisagreement(nautical, Feet) is null, "an unknown unit system");
            run.True(DeliveryHeader.DisplayDisagreement(undeclared.Delivery, Meters) is null, "no delivery block");
        });

        return run.Report("delivery header");
    }

    private static DeliveryFacts Read(string delivery)
    {
        BundleManifest manifest = BundleManifestReader.Parse(
            """{"version": "1.0.0", "hosts": {"unreal": {}}, "delivery": """ + delivery + "}");
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"fixture refused: {manifest.Error}");
        }

        return manifest.Delivery;
    }
}
