// The spike's Civil 3D half: the zone read and compare, what AutoCAD's coordinate library says each
// zone code is in EPSG and whether the zone is the drawing's geographic location, then a TIN surface
// from each DEM the driver wrote, once through AddDEMFile(path) and once through its coordinate-system
// overload, and one from each LandXML it wrote through CreateFromLandXML. Every call is recorded as
// worked, did nothing or threw, in the host's own words.
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Settings;

namespace MantlePlace.AutoCad.Civil3dSpike;

public static class Civil3dPart
{
    private static string Env(string name) => Environment.GetEnvironmentVariable("MANTLEPLACE_C3D_PROBE_" + name) ?? "";

    public static void Run(Action<string> line)
    {
        string zone = Env("ZONE");
        string mismatch = Env("MISMATCH_ZONE");
        string demZone = Env("DEM_ZONE");
        string[] dems = Env("DEMS").Split(';', StringSplitOptions.RemoveEmptyEntries);
        string[] landXmls = Env("LANDXMLS").Split(';', StringSplitOptions.RemoveEmptyEntries);

        line("## Zone");
        line("");
        line($"Order zone `{zone}`, mismatch zone `{mismatch}`.");
        line("");

        CivilDocument? civil = Call(line, "`CivilApplication.ActiveDocument`", () => CivilApplication.ActiveDocument, d => "a CivilDocument");
        if (civil == null)
        {
            return;
        }

        SettingsUnitZone? unitZone = Call(line, "`Settings.DrawingSettings.UnitZoneSettings`", () => civil.Settings.DrawingSettings.UnitZoneSettings, z => "a SettingsUnitZone");
        if (unitZone == null)
        {
            return;
        }

        string? Read(string when) => Call(line, $"`CoordinateSystemCode` read, {when}", () => unitZone.CoordinateSystemCode, code => $"`{code}`");

        string? fresh = Read("fresh drawing");
        Compare(line, fresh, zone);
        Read("after that compare (unchanged?)");
        GeoLocation(line, "fresh drawing");

        Call(line, "`SettingsUnitZone.GetAllCodes()`", SettingsUnitZone.GetAllCodes, codes =>
            $"{codes.Length} codes; `{zone}` among them: {codes.Contains(zone)}; `{demZone}` among them: {codes.Contains(demZone)}{(codes.Length > 0 ? $"; first `{codes[0]}`" : "")}"
            + (codes.Contains(zone) || zone.Length < 4 ? "" : $"; codes starting `{zone[..4]}`: {string.Join(", ", codes.Where(c => c.StartsWith(zone[..4], StringComparison.Ordinal)).Take(12))}"));
        Call(line, $"`SettingsUnitZone.GetCoordinateSystemByCode(\"{zone}\")`", () => SettingsUnitZone.GetCoordinateSystemByCode(zone), cs => cs == null ? "null" : $"code `{cs.Code}`, datum `{cs.Datum}`, projection `{cs.Projection}`");

        line("");
        line("Fixture only, never the plugin's behaviour: the scratch drawing (never saved) is given the order's zone so the match and mismatch can be compared.");
        line("");
        Call(line, $"`CoordinateSystemCode` set to `{zone}` (fixture)", () =>
        {
            unitZone.CoordinateSystemCode = zone;
            return true;
        }, _ => "no exception");
        string? set = Read("after the fixture");
        Compare(line, set, zone);
        Compare(line, set, mismatch);
        Read("after both compares (unchanged?)");
        GeoLocation(line, "after the fixture");

        line("");
        line("## Zone codes in EPSG");
        line("");
        foreach (string code in new[] { fresh ?? "", zone, mismatch, demZone })
        {
            Call(line, $"`GeoCoordinateSystem.Create(\"{code}\")`", () => GeoCoordinateSystem.Create(code), cs => $"ID `{cs.ID}`, EPSG {cs.EPSGcode}, unit `{cs.UnitScale}`");
        }

        line("");
        line("## Surface");
        line("");
        line($"DEM zone `{demZone}`; the drawing's zone is `{set}`.");
        line("");
        foreach (string dem in dems)
        {
            Surface(line, dem, null);
            Surface(line, dem, demZone);
        }

        line("");
        line("## LandXML surface");
        line("");
        foreach (string landXml in landXmls)
        {
            LandXmlSurface(line, landXml);
        }

        Read("after the surfaces (unchanged?)");
    }

    private static void GeoLocation(Action<string> line, string when)
    {
        Database db = Application.DocumentManager.MdiActiveDocument.Database;
        Call(line, $"`Database.GeoDataObject`, {when}", () =>
        {
            ObjectId id;
            try
            {
                id = db.GeoDataObject;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                return $"none (threw {ex.ErrorStatus})";
            }

            if (id.IsNull)
            {
                return "none (null id)";
            }

            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            var data = (GeoLocationData)tr.GetObject(id, OpenMode.ForRead);
            string cs = data.CoordinateSystem ?? "";
            return $"a GeoLocationData, coordinate system `{(cs.Length > 60 ? cs[..60] + "..." : cs)}`";
        }, s => s);
    }

    private static void LandXmlSurface(Action<string> line, string path)
    {
        string label = $"`TinSurface.CreateFromLandXML(db, name, path, \"Terrain\")`, {Path.GetFileName(path)}";
        Database db = Application.DocumentManager.MdiActiveDocument.Database;
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            ObjectId id = TinSurface.CreateFromLandXML(db, "MP probe landxml " + Path.GetFileNameWithoutExtension(path), path, "Terrain");
            var surface = (TinSurface)tr.GetObject(id, OpenMode.ForRead);
            GeneralSurfaceProperties p = surface.GetGeneralProperties();
            string verdict = p.NumberOfPoints > 0 ? "worked" : "did nothing";
            line($"- {label}: **{verdict}**, {p.NumberOfPoints} points, X {p.MinimumCoordinateX:F2} to {p.MaximumCoordinateX:F2}, Y {p.MinimumCoordinateY:F2} to {p.MaximumCoordinateY:F2}, Z {p.MinimumElevation:F2} to {p.MaximumElevation:F2}");
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            line($"- {label}: **threw** `{ex.GetType().Name}: {ex.Message}`");
        }
    }

    private static void Compare(Action<string> line, string? drawing, string order)
    {
        ZoneResult result = ZoneCheck.Compare(drawing, order);
        line($"- Compare drawing `{drawing}` with `{order}`: **{result.Verdict}**, {result.Reason}");
    }

    private static void Surface(Action<string> line, string dem, string? code)
    {
        string label = code == null
            ? $"`AddDEMFile(path)`, {Path.GetFileName(dem)}"
            : $"`AddDEMFile(path, \"{code}\", false, 0)`, {Path.GetFileName(dem)}";
        Database db = Application.DocumentManager.MdiActiveDocument.Database;
        try
        {
            using Transaction tr = db.TransactionManager.StartTransaction();
            string name = $"MP probe {Path.GetExtension(dem).TrimStart('.')} {(code == null ? "plain" : "cs")}";
            ObjectId id = TinSurface.Create(db, name);
            var surface = (TinSurface)tr.GetObject(id, OpenMode.ForWrite);
            if (code == null)
            {
                surface.DEMFilesDefinition.AddDEMFile(dem);
            }
            else
            {
                surface.DEMFilesDefinition.AddDEMFile(dem, code, false, 0.0);
            }

            surface.Rebuild();
            GeneralSurfaceProperties p = surface.GetGeneralProperties();
            string verdict = p.NumberOfPoints > 0 ? "worked" : "did nothing";
            line($"- {label}: **{verdict}**, {p.NumberOfPoints} points, X {p.MinimumCoordinateX:F2} to {p.MaximumCoordinateX:F2}, Y {p.MinimumCoordinateY:F2} to {p.MaximumCoordinateY:F2}, Z {p.MinimumElevation:F2} to {p.MaximumElevation:F2}");
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            line($"- {label}: **threw** `{ex.GetType().Name}: {ex.Message}`");
        }
    }

    private static T? Call<T>(Action<string> line, string what, Func<T> call, Func<T, string> describe)
    {
        try
        {
            T value = call();
            line(value == null ? $"- {what}: **did nothing** (null)" : $"- {what}: **worked**, {describe(value)}");
            return value;
        }
        catch (System.Exception ex)
        {
            line($"- {what}: **threw** `{ex.GetType().Name}: {ex.Message}`");
            return default;
        }
    }
}
