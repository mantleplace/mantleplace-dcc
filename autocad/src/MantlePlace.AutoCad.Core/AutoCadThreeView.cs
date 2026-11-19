using MantlePlace.Core;

namespace MantlePlace.AutoCad;

/// <summary>
/// An MPB 3.x bundle as the AutoCAD plan reads it: the block a 2.x bundle carried under
/// <c>hosts.autocad</c>, made from the files 3.0 promises in its place (<c>spec/format.md</c>, the A
/// rows of the 2.x-to-3.0 table), found by name in the catalog and in the frame and unit the manifest
/// states for each file's family. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Every value is stated and none is derived.</b> A 3.0 manifest states two frames once; every file
/// this plugin places is in the georeferenced family, so its coordinates are in the order's CRS and its
/// unit is the georeferenced frame's (A02, A04, A10). The local site frame is never read: an AutoCAD
/// drawing takes the order's coordinates, and nothing here converts one frame to the other. A CRS the
/// manifest names only as PROJJSON or WKT2 refuses the import, never taken apart for a code.
/// </para>
/// <para>
/// The 2.x parts and what each reads in 3.0: the contours, <see cref="ContoursPath"/>; the aerial,
/// <see cref="AerialPath"/> at the rectangle its catalog entry states in the order's CRS, aligned with that
/// system's axes, and only when the entry states the georeferenced frame (A14);
/// each public-record layer, a <c>Records/*.dxf</c> named by its file (A06); and the site linework,
/// <see cref="LineworkPath"/>; and the Civil 3D surface, <see cref="SurfacePath"/>, which Civil 3D builds
/// once the run has checked the file's own units and coordinate system against the order's
/// (<see cref="LandXmlSurface"/>). A file the manifest lists as not held is skipped with the manifest's
/// reason, verbatim, and one the catalog does not name is skipped saying so.
/// </para>
/// <para>
/// There are no readiness verdicts in 3.0 (A15): a file the catalog names is ready, one it does not is
/// not held. So every part named here is planned exactly as a 2.x part whose verdict says present, by
/// <see cref="AutoCadImportPlanner"/>, which still checks each file's frame and units against the order's.
/// </para>
/// </remarks>
public static class AutoCadThreeView
{
    /// <summary>The contours (A05).</summary>
    public const string ContoursPath = "Elevation/Contours.dxf";

    /// <summary>The terrain's LandXML, the Civil 3D surface's source, absolute in the order's CRS and units (§4, §6).</summary>
    public const string SurfacePath = "Elevation/Terrain.landxml";

    /// <summary>The georeferenced aerial image, placed at the rectangle it covers in the order's CRS (A14).</summary>
    public const string AerialPath = "Imagery/Imagery.tif";

    /// <summary>The site linework (A05).</summary>
    public const string LineworkPath = "Site/Site.dxf";

    /// <summary>The folder every public-record layer's file is in.</summary>
    public const string RecordsFolder = "Records/";

    /// <summary>A public-record layer's file's extension: the DXF, which this plugin places.</summary>
    public const string RecordsExtension = ".dxf";

    /// <summary>The format word a 3.0 aerial is planned with: a GeoTIFF, which AutoCAD inserts as a raster image.</summary>
    public const string GeoTiffFormat = "geotiff";

    /// <summary>The frame a raster's rectangle is stated in when it is in the order's CRS.</summary>
    public const string GeoreferencedRasterFrame = "georeferenced";

    private const string AbsoluteProjected = "absolute_projected";
    private const string LocalEnu = "local_enu";
    private const string AbsoluteHeights = "absolute";

    /// <summary>Whether <paramref name="manifest"/> is a 3.x bundle's: its manifest was found at the root.</summary>
    public static bool IsThree(BundleManifest? manifest) =>
        manifest?.Contents is { } contents
        && string.Equals(contents.ManifestPath, BundleReader.ManifestPath, StringComparison.Ordinal);

    /// <summary>Reads a 3.x bundle's reading into the block the plan takes. Never throws.</summary>
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

        // The surface is Civil 3D's TIN from the terrain's LandXML, in the georeferenced family (§4): the
        // order's CRS and units, as every other file here. The run checks the file's own Units and
        // CoordinateSystem against that frame before Civil 3D reads it (LandXmlSurface).
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

        // The one host part 3.0 implies: the surface is built by Civil 3D, so plain AutoCAD says it needs
        // it, as a 2.x verdict's `requires` made it say.
        PartReadiness surfaceHeld = held with { Requires = AutoCadImportPlanner.Civil3dPart };

        return new AutoCadBlockRead
        {
            Manifest = manifest,
            Block = new AutoCadBlock
            {
                // The order's frame alone: projected, in the georeferenced frame's CRS and units (A02, A04).
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

    /// <summary>The order's units and vertical datum, as the georeferenced frame states them.</summary>
    private sealed record Frame(string Horizontal, string Vertical, string Datum);

    /// <summary>
    /// The pointer for the file the catalog names <paramref name="path"/>, found or not: a file the catalog
    /// names and the bundle lacks still gets one, so the run says the entry is missing. <c>null</c> when
    /// the catalog does not name it.
    /// </summary>
    private static AutoCadPointer? Pointer(BundleContents contents, string path, Frame frame, string format = "", AerialInsertion? insertion = null, int width = 0, int height = 0)
    {
        BundleFile? file = contents.Find(path);
        if (file is null)
        {
            if (!contents.Skipped.Any(s => s.Why == SkipCause.FileMissing && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            // Every file this plugin reads is in the georeferenced family by its extension (§4).
            file = new BundleFile(path, FileFamily.Georeferenced, null, null);
        }

        // The family settles the frame and the unit (A10, A11): a georeferenced file is absolute in the
        // order's CRS, in the georeferenced frame's unit. A file of any other family states a frame the
        // plan refuses to place in the order's.
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

    /// <summary>
    /// The aerial: the GeoTIFF at the rectangle its catalog entry states in the order's CRS (A14), aligned
    /// with that system's axes; the plugin derives no rotation. It fails closed: an entry outside the
    /// georeferenced family, or whose raster frame is not stated as the georeferenced one, is not placed.
    /// <paramref name="why"/> is the sentence for an image that cannot be placed as stated.
    /// </summary>
    private static AutoCadPointer? Aerial(BundleContents contents, Frame frame, out string? why)
    {
        why = null;
        string label = AutoCadImportPlanner.LabelOf(AutoCadPart.Aerial);
        BundleFile? file = contents.Find(AerialPath);
        RasterGrid? raster = file?.Raster;

        // A catalog entry is placed only as it states itself: in the georeferenced family, its rectangle
        // in the georeferenced frame. A file the catalog names and the bundle lacks has no entry here,
        // and is planned so the run says it is missing.
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

    /// <summary>
    /// Every public-record layer's file the bundle names or promises, sorted: those its catalog names, those
    /// it lists as not held, and those a 3.0 download asks for (<see cref="AutoCadImportPlan.ThreeFiles"/>).
    /// </summary>
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

    /// <summary>Whether <paramref name="path"/> is a public-record layer's DXF, directly under <see cref="RecordsFolder"/>.</summary>
    private static bool IsRecord(string path) =>
        path.StartsWith(RecordsFolder, StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(RecordsExtension, StringComparison.OrdinalIgnoreCase)
        && path.IndexOf('/', RecordsFolder.Length) < 0
        && path.Length > RecordsFolder.Length + RecordsExtension.Length;

    /// <summary>What a part whose file is not named is told: the manifest's reason when it lists one.</summary>
    private static string Absence(BundleManifest manifest, AutoCadPart kind, string path) =>
        $"{AutoCadImportPlanner.LabelOf(kind)}: not in this bundle. {Why(manifest, path)}";

    /// <summary>Why one record layer is not placed, in the words that go in its brackets.</summary>
    private static string RecordAbsence(BundleManifest manifest, string path) =>
        NotHeld(manifest, path) is { } skipped
            ? $"not held: {skipped.Reason ?? "no reason given"}"
            : "the catalog names no file for it, and the manifest gives no reason";

    /// <summary>The sentence that says why a file is not in the bundle, with the manifest's reason, verbatim.</summary>
    private static string Why(BundleManifest manifest, string path) =>
        NotHeld(manifest, path) is { } skipped
            ? $"It does not hold {path}: {skipped.Reason ?? "the manifest gives no reason"}."
            : $"Its catalog names no {path}, and the manifest gives no reason it does not hold one.";

    private static SkippedFile? NotHeld(BundleManifest manifest, string path) =>
        manifest.Contents?.Skipped.FirstOrDefault(s =>
            s.Why == SkipCause.NotHeld && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
}
