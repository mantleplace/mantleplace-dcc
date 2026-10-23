// The carrier spike's in-Revit probe (README.md beside ../). Loaded only by Invoke-CarrierProbe.ps1's
// temporary manifest, and inert unless that driver launched this Revit: it does nothing without
// MANTLEPLACE_CARRIER_PROBE_DIR. One mode per launch, so a cold read is a cold read:
//   geojson | gpkg | dxf   read every layer through that carrier (cold, timed), place each layer as
//                          one DirectShape of lines in a fresh project whose survey point is set as
//                          the plugin sets it, and read the placement back
//   ifc                    convert and link the site IFC the way the plugin does, then ask the
//                          converted model and both link placements where the content went
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.CarrierSpike;

/// <summary>Records every warning a transaction raises, in Revit's own words, and lets it go on.</summary>
internal sealed class WarningRecorder(List<string> seen) : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
        {
            seen.Add($"{failure.GetSeverity()}: {failure.GetDescriptionText()}");
            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                failuresAccessor.DeleteWarning(failure);
            }
        }

        return FailureProcessingResult.Continue;
    }
}

public sealed class CarrierProbe : IExternalApplication
{
    private static readonly StringBuilder Report = new();
    private static string _dir = string.Empty;

    private static string Env(string name) => Environment.GetEnvironmentVariable("MANTLEPLACE_CARRIER_PROBE_" + name) ?? string.Empty;

    public Result OnStartup(UIControlledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _dir = Env("DIR");
        if (_dir.Length == 0)
        {
            return Result.Succeeded;
        }

        File.WriteAllText(Path.Combine(_dir, "started.txt"), DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        application.ControlledApplication.ApplicationInitialized += (sender, _) => Run((Application)sender!);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static void Say(string line) => Report.AppendLine(line);

    private static string F(double value, string format = "0.000") => value.ToString(format, CultureInfo.InvariantCulture);

    private static void Run(Application app)
    {
        string mode = Env("MODE");
        string outcome = "ok";
        try
        {
            Say($"# Carrier probe: {mode}, Revit {app.VersionNumber} ({app.VersionBuild}), .NET {Environment.Version}");
            Say(string.Empty);
            CarrierComparison bundle = new(Env("BUNDLE"), Env("CARRIERS"));
            if (mode == "ifc")
            {
                RunIfc(app, bundle);
            }
            else
            {
                RunVectors(app, bundle, mode switch { "gpkg" => Carrier.GeoPackage, "dxf" => Carrier.Dxf, _ => Carrier.GeoJson });
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = "error";
            Say($"FAILED: {ex}");
        }

        File.WriteAllText(Path.Combine(_dir, "result.md"), Report.ToString());
        File.WriteAllText(Path.Combine(_dir, "done.txt"), outcome);
    }

    // ---- vectors --------------------------------------------------------------------------------

    private static void RunVectors(Application app, CarrierComparison bundle, Carrier carrier)
    {
        // Cold first: the first read in this process pays for loading winsqlite3.dll and for the JIT,
        // which is what a curator's first import pays.
        Stopwatch cold = Stopwatch.StartNew();
        List<CarrierRead> reads = [.. bundle.Layers.Select(layer => bundle.Read(carrier, layer))];
        double coldMs = cold.Elapsed.TotalMilliseconds;
        Say($"## {carrier}: cold read of every layer inside Revit, {F(coldMs, "0.0")} ms");
        if (carrier == Carrier.GeoPackage)
        {
            Say($"SQLite loaded in Revit's process: winsqlite3 {WinSqlite.Version}");
        }

        Say(string.Empty);
        Say("| layer | features | refusal | read ms |");
        Say("|---|---|---|---|");
        foreach (CarrierRead read in reads)
        {
            Say($"| {read.Layer} | {read.Features.Count} | {read.Refusal ?? "none"} | {F(read.Milliseconds, "0.0")} |");
        }

        Say(string.Empty);
        Say("## Parity against today's GeoJSON, read again in this process");
        Say(string.Empty);
        Say(bundle.Table());

        Document document = app.NewProjectDocument(Autodesk.Revit.DB.UnitSystem.Imperial);
        try
        {
            Stopwatch placing = Stopwatch.StartNew();
            Dictionary<string, (ElementId Id, int Curves, int Short)> shapes = [];
            List<string> warnings = [];
            using (Transaction transaction = new(document, "carrier probe: place"))
            {
                Recording(transaction, warnings);
                transaction.Start();
                SetSurveyPoint(document, bundle.Frame);
                foreach (CarrierRead read in reads)
                {
                    shapes[read.Layer] = Place(document, app.ShortCurveTolerance, read.Features);
                }

                transaction.Commit();
            }

            Warnings("placing", warnings);
            Say($"## Placed in a fresh imperial project, {F(placing.Elapsed.TotalMilliseconds, "0.0")} ms for every layer");
            Say(string.Empty);
            Say("| layer | lines | too short to draw | bounding box min, internal ft | max, internal ft | min in shared coordinates, survey ft |");
            Say("|---|---|---|---|---|---|");
            Transform toShared = document.ActiveProjectLocation.GetTotalTransform().Inverse;
            foreach ((string layer, (ElementId id, int curves, int tooShort)) in shapes)
            {
                BoundingBoxXYZ? box = id == ElementId.InvalidElementId ? null : document.GetElement(id).get_BoundingBox(null);
                Say(box is null
                    ? $"| {layer} | {curves} | {tooShort} | none | none | none |"
                    : $"| {layer} | {curves} | {tooShort} | {Xyz(box.Min)} | {Xyz(box.Max)} | {SurveyFeet(toShared.OfPoint(box.Min))} |");
            }

            SiteFeature? sample = reads.SelectMany(read => read.Features).FirstOrDefault();
            if (sample is not null)
            {
                SiteVertex v = sample.Vertices[0];
                XYZ at = Internal(v);
                Say(string.Empty);
                Say($"First vertex of the first layer: internal {Xyz(at)} ft; shared {SurveyFeet(toShared.OfPoint(at))} survey ft "
                    + $"(the file wrote it {F(bundle.Frame.Origin.Easting!.Value + (v.EastM / LinearUnits.MetresPerUnit(bundle.FileUnit)))}, "
                    + $"{F(bundle.Frame.Origin.Northing!.Value + (v.NorthM / LinearUnits.MetresPerUnit(bundle.FileUnit)))}).");
            }
        }
        finally
        {
            document.Close(false);
        }
    }

    private static (ElementId Id, int Curves, int Short) Place(Document document, double shortest, IReadOnlyList<SiteFeature> features)
    {
        List<GeometryObject> lines = [];
        int tooShort = 0;
        foreach (SiteFeature feature in features)
        {
            int count = feature.Vertices.Count;
            int segments = feature.IsClosed ? count : count - 1;
            for (int i = 0; i < segments; i++)
            {
                XYZ a = Internal(feature.Vertices[i]);
                XYZ b = Internal(feature.Vertices[(i + 1) % count]);
                if (a.DistanceTo(b) < shortest)
                {
                    tooShort++;
                    continue;
                }

                lines.Add(Line.CreateBound(a, b));
            }
        }

        if (lines.Count == 0)
        {
            return (ElementId.InvalidElementId, 0, tooShort);
        }

        DirectShape shape = DirectShape.CreateElement(document, new ElementId(BuiltInCategory.OST_GenericModel));
        shape.SetShape(lines);
        return (shape.Id, lines.Count, tooShort);
    }

    private static XYZ Internal(SiteVertex v)
        => new(Feet(v.EastM), Feet(v.NorthM), Feet(v.ElevationM ?? 0.0));

    private static double Feet(double metres) => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

    private static string Xyz(XYZ p) => $"({F(p.X)}, {F(p.Y)}, {F(p.Z)})";

    private static string SurveyFeet(XYZ p)
        => $"({F(UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.UsSurveyFeet))}, {F(UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.UsSurveyFeet))})";

    /// <summary>The survey point the plugin applies: the manifest's origin verbatim, elevation zero (RevitBundleImporter.SharedCoordinates).</summary>
    private static void SetSurveyPoint(Document document, SiteFrame frame)
    {
        ForgeTypeId unit = frame.Origin.LinearUnit switch
        {
            LinearUnit.UsSurveyFoot => UnitTypeId.UsSurveyFeet,
            LinearUnit.InternationalFoot => UnitTypeId.Feet,
            _ => UnitTypeId.Meters,
        };
        ProjectPosition position = new(
            UnitUtils.ConvertToInternalUnits(frame.Origin.Easting!.Value, unit),
            UnitUtils.ConvertToInternalUnits(frame.Origin.Northing!.Value, unit),
            0.0,
            0.0);
        document.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, position);
    }

    // ---- the linked site IFC --------------------------------------------------------------------

    private static void RunIfc(Application app, CarrierComparison bundle)
    {
        string ifc = Path.Combine(Env("BUNDLE"), "Site", "Site.ifc");
        string companion = Path.Combine(_dir, $"Site.{app.VersionNumber}.rvt");

        Stopwatch converting = Stopwatch.StartNew();
        using (IFCImportOptions options = new() { Intent = IFCImportIntent.Reference, AutoJoin = false })
        {
            Document converted = app.OpenIFCDocument(ifc, options);
            try
            {
                Say($"## The converted site model ({F(converting.Elapsed.TotalSeconds, "0.0")} s to convert, Reference intent, as the plugin does)");
                Say(string.Empty);
                Locations(converted, "converted model");
                Contents(converted, Transform.Identity, "in the converted model's own internal coordinates");
                converted.SaveAs(companion, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally
            {
                converted.Close(false);
            }
        }

        CarrierRead footprints = bundle.Read(Carrier.GeoPackage, CarrierComparison.Footprints);
        (XYZ min, XYZ max) = PlanBox(footprints.Features);
        Say(string.Empty);
        Say($"The GeoPackage footprints ({footprints.Features.Count}) in the host's frame: plan {Xyz(min)} to {Xyz(max)} internal ft.");

        Document host = app.NewProjectDocument(Autodesk.Revit.DB.UnitSystem.Imperial);
        try
        {
            List<string> warnings = [];
            using Transaction transaction = new(host, "carrier probe: link IFC");
            Recording(transaction, warnings);
            transaction.Start();
            SetSurveyPoint(host, bundle.Frame);
            SetSiteLocation(host, bundle.Frame);
            Locations(host, "host, survey point and site location set as the plugin sets them, before the link");
            Say($"- host, before the link: GIS coordinate system {(string.IsNullOrEmpty(host.SiteLocation.GeoCoordinateSystemId) ? "none" : host.SiteLocation.GeoCoordinateSystemId)}");
            LinkLoadResult result = RevitLinkType.CreateFromIFC(host, ifc, companion, false, new RevitLinkOptions(false));
            Say(string.Empty);
            Say($"## Linked into a fresh imperial project whose survey point is the manifest's origin: {result.LoadResult}");
            Locations(host, "host");
            if (result.ElementId != ElementId.InvalidElementId)
            {
                RevitLinkInstance origin = RevitLinkInstance.Create(host, result.ElementId);
                Describe(host, origin, "Origin to origin (what the plugin does)");
                MatchBuildings(origin, footprints.Features);
            }

            Say(string.Empty);
            Say($"Committed: {transaction.Commit()}.");
            Warnings("linking", warnings);
            Locations(host, "host, after the commit");
            Say($"- host, after the link: GIS coordinate system {(string.IsNullOrEmpty(host.SiteLocation.GeoCoordinateSystemId) ? "none" : host.SiteLocation.GeoCoordinateSystemId)}");

            List<string> again = [];
            using Transaction relocate = new(host, "carrier probe: site location after the link");
            Recording(relocate, again);
            relocate.Start();
            SetSiteLocation(host, bundle.Frame);
            Say($"Site location set again after the link: {relocate.Commit()}.");
            Warnings("setting the site location after the link", again);
            Locations(host, "host, site location set after the link");

            if (result.ElementId != ElementId.InvalidElementId)
            {
                List<string> sharedWarnings = [];
                using Transaction second = new(host, "carrier probe: a second instance by shared coordinates");
                Recording(second, sharedWarnings);
                second.Start();
                try
                {
                    RevitLinkInstance shared = RevitLinkInstance.Create(host, result.ElementId, ImportPlacement.Shared);
                    Describe(host, shared, "A second instance, by shared coordinates (ImportPlacement.Shared), in its own transaction");
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    Say($"### By shared coordinates (ImportPlacement.Shared): refused, {ex.GetType().Name}: {ex.Message}");
                }

                Say($"Committed: {second.Commit()}.");
                Warnings("placing by shared coordinates", sharedWarnings);
                Locations(host, "host, after the shared-coordinates instance");
            }
        }
        finally
        {
            host.Close(false);
        }
    }

    private static void Recording(Transaction transaction, List<string> warnings)
    {
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new WarningRecorder(warnings));
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
    }

    private static void Warnings(string what, List<string> warnings)
    {
        Say(string.Empty);
        Say(warnings.Count == 0 ? $"Revit raised no failure while {what}." : $"Revit raised {warnings.Count} while {what}:");
        foreach (string warning in warnings.Distinct())
        {
            Say($"- {warning}");
        }

        Say(string.Empty);
    }

    /// <summary>The manifest's origin as the site location, as RevitBundleImporter.SiteLocation does.</summary>
    private static void SetSiteLocation(Document document, SiteFrame frame)
    {
        document.SiteLocation.Latitude = frame.Origin.Lat!.Value * Math.PI / 180.0;
        document.SiteLocation.Longitude = frame.Origin.Lon!.Value * Math.PI / 180.0;
    }

    /// <summary>
    /// Each linked building against the nearest GeoPackage footprint, centre to centre in plan: the
    /// placement question asked of every building rather than of one bounding box.
    /// </summary>
    private static void MatchBuildings(RevitLinkInstance instance, IReadOnlyList<SiteFeature> footprints)
    {
        if (instance.GetLinkDocument() is not { } linked)
        {
            return;
        }

        Transform transform = instance.GetTotalTransform();
        List<XYZ> printed = [];
        foreach (SiteFeature footprint in footprints.Where(feature => !feature.IsHole))
        {
            (XYZ min, XYZ max) = PlanBox([footprint]);
            printed.Add((min + max) / 2.0);
        }

        List<double> distances = [];
        foreach (Element element in new FilteredElementCollector(linked).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType())
        {
            if (element.get_BoundingBox(null) is not { } box)
            {
                continue;
            }

            XYZ centre = transform.OfPoint((box.Min + box.Max) / 2.0);
            XYZ plan = new(centre.X, centre.Y, 0.0);
            distances.Add(printed.Count == 0 ? double.NaN : printed.Min(p => p.DistanceTo(plan)));
        }

        distances.Sort();
        if (distances.Count == 0)
        {
            Say("No linked building to match.");
            return;
        }

        double At(double q) => distances[Math.Min(distances.Count - 1, (int)Math.Floor(q * distances.Count))];
        Say(string.Empty);
        Say($"Each of the {distances.Count} linked buildings against the nearest of {printed.Count} GeoPackage footprints, plan centre to centre: "
            + $"median {F(At(0.5))} ft, 90th percentile {F(At(0.9))} ft, worst {F(distances[^1])} ft; "
            + $"{distances.Count(d => d < 1.0)} within 1 ft, {distances.Count(d => d < 0.1)} within 0.1 ft.");
    }

    private static void Describe(Document host, RevitLinkInstance instance, string title)
    {
        host.Regenerate();
        Transform transform = instance.GetTotalTransform();
        Say(string.Empty);
        Say($"### {title}");
        Say($"Link transform origin {Xyz(transform.Origin)} internal ft, basis X {Xyz(transform.BasisX)}.");
        if (instance.GetLinkDocument() is { } linked)
        {
            Contents(linked, transform, "in the host's internal coordinates");
        }
    }

    private static void Locations(Document document, string what)
    {
        ProjectPosition position = document.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        BasePoint survey = BasePoint.GetSurveyPoint(document);
        BasePoint projectBase = BasePoint.GetProjectBasePoint(document);
        Say($"- {what}: project position at the internal origin E/W {F(UnitUtils.ConvertFromInternalUnits(position.EastWest, UnitTypeId.UsSurveyFeet))}, "
            + $"N/S {F(UnitUtils.ConvertFromInternalUnits(position.NorthSouth, UnitTypeId.UsSurveyFeet))}, "
            + $"elevation {F(UnitUtils.ConvertFromInternalUnits(position.Elevation, UnitTypeId.UsSurveyFeet))} survey ft, angle {F(position.Angle, "0.000000")} rad");
        Say($"- {what}: survey point shared {Xyz(survey.SharedPosition)} at internal {Xyz(survey.Position)}; project base point at internal {Xyz(projectBase.Position)} (ft)");
        Say($"- {what}: site location {document.SiteLocation.PlaceName} lat {F(document.SiteLocation.Latitude * 180.0 / Math.PI, "0.000000")}, lon {F(document.SiteLocation.Longitude * 180.0 / Math.PI, "0.000000")}");
    }

    private static void Contents(Document document, Transform transform, string frame)
    {
        Dictionary<string, (int Count, XYZ Min, XYZ Max)> byCategory = new(StringComparer.Ordinal);
        foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            if (element.Category is null || element.get_BoundingBox(null) is not { } box || element is BasePoint)
            {
                continue;
            }

            XYZ a = transform.OfPoint(box.Min);
            XYZ b = transform.OfPoint(box.Max);
            XYZ low = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
            XYZ high = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
            string name = element.Category.Name;
            byCategory[name] = byCategory.TryGetValue(name, out var seen)
                ? (seen.Count + 1, new XYZ(Math.Min(seen.Min.X, low.X), Math.Min(seen.Min.Y, low.Y), Math.Min(seen.Min.Z, low.Z)), new XYZ(Math.Max(seen.Max.X, high.X), Math.Max(seen.Max.Y, high.Y), Math.Max(seen.Max.Z, high.Z)))
                : (1, low, high);
        }

        Say(string.Empty);
        Say($"Elements by category, bounding boxes {frame} (ft):");
        Say(string.Empty);
        Say("| category | elements | min | max |");
        Say("|---|---|---|---|");
        foreach ((string name, (int count, XYZ min, XYZ max)) in byCategory.OrderByDescending(entry => entry.Value.Count))
        {
            Say($"| {name} | {count} | {Xyz(min)} | {Xyz(max)} |");
        }
    }

    private static (XYZ Min, XYZ Max) PlanBox(IReadOnlyList<SiteFeature> features)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (SiteVertex v in features.SelectMany(feature => feature.Vertices))
        {
            minX = Math.Min(minX, v.EastM);
            minY = Math.Min(minY, v.NorthM);
            maxX = Math.Max(maxX, v.EastM);
            maxY = Math.Max(maxY, v.NorthM);
        }

        return (new XYZ(Feet(minX), Feet(minY), 0), new XYZ(Feet(maxX), Feet(maxY), 0));
    }
}
