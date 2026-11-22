using MantlePlace.Core;

namespace MantlePlace.AutoCad;

public static class AutoCadThreeView
{
    public const string ContoursPath = "Elevation/Contours.dxf";

    public const string SurfacePath = "Elevation/Terrain.landxml";

    public const string AerialPath = "Imagery/Imagery.tif";

    public const string LineworkPath = "Site/Site.dxf";

    public const string RecordsFolder = "Records/";

    public const string RecordsExtension = ".dxf";

    public const string GeoTiffFormat = "geotiff";

    public const string GeoreferencedRasterFrame = "georeferenced";

    private const string AbsoluteProjected = "absolute_projected";
    private const string LocalEnu = "local_enu";
    private const string AbsoluteHeights = "absolute";

    public static bool IsThree(BundleManifest? manifest) =>
        manifest?.Contents is { } contents
        && string.Equals(contents.ManifestPath, BundleReader.ManifestPath, StringComparison.Ordinal);

    public static AutoCadBlockRead Read(BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!manifest.IsValid)
        {
            return new AutoCadBlockRead { Manifest = manifest, Refusal = manifest.Error };
        }

        if (manifest.Contents is not { Frames: { } frames } contents)
        {
            return new AutoCadBlockRead
            {
                Manifest = manifest,
                Refusal = "This bundle's Manifest.json states no frames, so there is no coordinate system to place it in. Nothing was imported.",
            };
        }

        GeoreferencedFrame order = frames.Georeferenced;
        if (string.IsNullOrWhiteSpace(order.HorizontalCrs.Code))
        {
            return new AutoCadBlockRead
            {
                Manifest = manifest,
                Refusal = "This bundle names the order's coordinate system only as a PROJJSON object or a WKT2 string. "
                    + "This plugin places an order only in a coordinate system named by its EPSG code, and takes no "
                    + "definition apart for one. Nothing was imported.",
            };
        }

        Frame frame = new(
            LinearUnits.ToManifestToken(order.HorizontalUnit.Unit),
            LinearUnits.ToManifestToken(order.VerticalUnit.Unit),
            HeightDatums.FromFrame(order.VerticalCrs, order.GeoidModel)
                ?? order.VerticalCrs.Code
                ?? "the vertical datum the order states");

        Dictionary<AutoCadPart, string> leftOut = [];

        AutoCadPointer? contours = Pointer(contents, ContoursPath, frame);
        if (contours is null)
        {
            leftOut[AutoCadPart.Contours] = Absence(manifest, AutoCadPart.Contours, ContoursPath);
        }

        AutoCadPointer? surface = Pointer(contents, SurfacePath, frame, AutoCadImportPlanner.LandXmlFormat);
        if (surface is null)
        {
            leftOut[AutoCadPart.Surface] = Absence(manifest, AutoCadPart.Surface, SurfacePath);
        }

        AutoCadPointer? aerial = Aerial(contents, frame, out string? aerialWhy);
        if (aerialWhy is not null)
        {
            leftOut[AutoCadPart.Aerial] = aerialWhy;
        }
        else if (aerial is null)
        {
            leftOut[AutoCadPart.Aerial] = Absence(manifest, AutoCadPart.Aerial, AerialPath);
        }

        List<AutoCadRecordLayer> records = [];
        List<string> recordsLeftOut = [];
        foreach (string path in RecordPaths(contents))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (Pointer(contents, path, frame) is { } pointer)
            {
                records.Add(new AutoCadRecordLayer(name, pointer));
            }
            else
            {
                recordsLeftOut.Add($"{name} ({RecordAbsence(manifest, path)})");
            }
        }

        if (records.Count == 0)
        {
            leftOut[AutoCadPart.Records] = recordsLeftOut.Count == 0
                ? $"{AutoCadImportPlanner.LabelOf(AutoCadPart.Records)}: not in this bundle. Its catalog names no {RecordsFolder}*{RecordsExtension} file."
                : $"{AutoCadImportPlanner.LabelOf(AutoCadPart.Records)}: not in this bundle. It holds none of them: {string.Join(", ", recordsLeftOut)}.";
        }

        AutoCadPointer? linework = Pointer(contents, LineworkPath, frame);
        if (linework is null)
        {
            leftOut[AutoCadPart.Linework] = Absence(manifest, AutoCadPart.Linework, LineworkPath);
        }

        PartReadiness held = new(true, true, null, null);
        PartReadiness notHeld = new(true, false, null, null);

        PartReadiness surfaceHeld = held with { Requires = AutoCadImportPlanner.Civil3dPart };

        return new AutoCadBlockRead
        {
            Manifest = manifest,
            Block = new AutoCadBlock
            {
                FileFrame = new AutoCadFileFrame(
                    AutoCadFileFrame.Projected,
                    order.HorizontalCrs.Code.Trim(),
                    string.Empty,
                    null,
                    null,
                    string.Empty,
                    frame.Horizontal,
                    frame.Vertical),
                Contours = contours,
                Surface = surface,
                Aerial = aerial,
                Records = records.Count == 0 ? null : records,
                Linework = linework,
                ContoursReadiness = contours is null ? notHeld : held,
                SurfaceReadiness = surface is null ? notHeld : surfaceHeld,
                AerialReadiness = aerial is null ? notHeld : held,
                RecordsReadiness = records.Count == 0 ? notHeld : held,
                LineworkReadiness = linework is null ? notHeld : held,
                FromThree = true,
                LeftOut = leftOut,
                RecordsLeftOut = records.Count == 0 ? [] : recordsLeftOut,
            },
        };
    }

    private sealed record Frame(string Horizontal, string Vertical, string Datum);

    private static AutoCadPointer? Pointer(BundleContents contents, string path, Frame frame, string format = "", AerialInsertion? insertion = null, int width = 0, int height = 0)
    {
        BundleFile? file = contents.Find(path);
        if (file is null)
        {
            if (!contents.Skipped.Any(s => s.Why == SkipCause.FileMissing && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            file = new BundleFile(path, FileFamily.Georeferenced, null, null);
        }

        bool georeferenced = file.Family == FileFamily.Georeferenced;
        string unit = file.Unit == LinearUnit.Unspecified ? frame.Horizontal : LinearUnits.ToManifestToken(file.Unit);

        return new AutoCadPointer(
            file.Path,
            file.Sha256 ?? string.Empty,
            format,
            georeferenced ? AbsoluteProjected : LocalEnu,
            unit,
            frame.Vertical,
            AbsoluteHeights,
            frame.Datum,
            width,
            height,
            insertion);
    }

    private static AutoCadPointer? Aerial(BundleContents contents, Frame frame, out string? why)
    {
        why = null;
        string label = AutoCadImportPlanner.LabelOf(AutoCadPart.Aerial);
        BundleFile? file = contents.Find(AerialPath);
        RasterGrid? raster = file?.Raster;

        if (file is not null)
        {
            if (file.Family != FileFamily.Georeferenced)
            {
                why = $"{label}: not imported, because {AerialPath} is not in the georeferenced family, so its coordinates are "
                    + "not the order's, and the plugin does not reproject.";
                return null;
            }

            if (raster?.Frame is not { } stated)
            {
                why = $"{label}: not imported, because the catalog states no frame for {AerialPath}'s rectangle, so it is not "
                    + "shown to be in the order's coordinate system, and the plugin does not assume it is.";
                return null;
            }

            if (!string.Equals(stated, GeoreferencedRasterFrame, StringComparison.OrdinalIgnoreCase))
            {
                why = $"{label}: not imported, because the catalog states {AerialPath}'s rectangle in the \"{stated}\" frame, not the "
                    + "order's coordinate system, and the plugin does not reproject.";
                return null;
            }
        }

        AerialInsertion? insertion = null;
        if (raster?.Rectangle is { } r)
        {
            insertion = new AerialInsertion(r.Left, r.Bottom, r.Right - r.Left, r.Top - r.Bottom, 0);
        }
        else if (file is not null)
        {
            why = $"{label}: not imported. The bundle publishes no insertion for it: its catalog states no rectangle for "
                + $"{AerialPath}, and the plugin does not work one out.";
            return null;
        }

        return Pointer(contents, AerialPath, frame, GeoTiffFormat, insertion, raster?.Width ?? 0, raster?.Height ?? 0);
    }

    private static List<string> RecordPaths(BundleContents contents)
    {
        IEnumerable<string> named = contents.Files.Select(f => f.Path)
            .Concat(contents.Skipped.Select(s => s.Path))
            .Concat(AutoCadImportPlan.ThreeFiles);

        return [.. named
            .Where(IsRecord)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    private static bool IsRecord(string path) =>
        path.StartsWith(RecordsFolder, StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(RecordsExtension, StringComparison.OrdinalIgnoreCase)
        && path.IndexOf('/', RecordsFolder.Length) < 0
        && path.Length > RecordsFolder.Length + RecordsExtension.Length;

    private static string Absence(BundleManifest manifest, AutoCadPart kind, string path) =>
        $"{AutoCadImportPlanner.LabelOf(kind)}: not in this bundle. {Why(manifest, path)}";

    private static string RecordAbsence(BundleManifest manifest, string path) =>
        NotHeld(manifest, path) is { } skipped
            ? $"not held: {skipped.Reason ?? "no reason given"}"
            : "the catalog names no file for it, and the manifest gives no reason";

    private static string Why(BundleManifest manifest, string path) =>
        NotHeld(manifest, path) is { } skipped
            ? $"It does not hold {path}: {skipped.Reason ?? "the manifest gives no reason"}."
            : $"Its catalog names no {path}, and the manifest gives no reason it does not hold one.";

    private static SkippedFile? NotHeld(BundleManifest manifest, string path) =>
        manifest.Contents?.Skipped.FirstOrDefault(s =>
            s.Why == SkipCause.NotHeld && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
}
