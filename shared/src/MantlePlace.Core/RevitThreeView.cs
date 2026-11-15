using System.Globalization;

namespace MantlePlace.Core;

/// <summary>
/// One item an MPB 3.0 import plan leaves out, as the release notes for <c>revit-1.2.0</c> name it.
/// </summary>
/// <param name="Kind">
/// The step skipped for it, with <see cref="SkipReasonCode.NotInFormat"/> and <paramref name="Why"/>
/// as its reason; <c>null</c> for a fact a planned step goes without.
/// </param>
/// <param name="Item">What is left out, in the notes' words.</param>
/// <param name="Why">Why, in the notes' words and the skip's.</param>
public sealed record ThreeLeftOutItem(ImportStepKind? Kind, string Item, string Why);

/// <summary>
/// An MPB 3.x reading, put in the shape <see cref="BundleImportPlanner"/> plans a 2.x bundle from, so
/// every step is decided by the same code for both majors. Pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each step's file is found by its promised name in the catalog</b> (<c>spec/format.md</c> §9.5,
/// §13): the terrain's triangles in <c>Elevation/Terrain.dxf</c>, the site model in
/// <c>Site/Site.ifc</c>, each vector layer in its one GeoPackage, the trees in <c>Landcover/Trees.csv</c>
/// and the drape in <c>Imagery/Imagery.png</c>. A file is pointed at when the catalog names it, so one
/// the bundle lacks is the planner's "no such entry"; a file the catalog does not name has no pointer,
/// and <see cref="BundleImportPlanner"/> says whether the manifest lists it as not held.
/// </para>
/// <para>
/// <b>Each file is in the frame its family settles</b> (§4), recorded on the pointer
/// (<see cref="BundleArtifact.Family"/>): a georeferenced file is absolute in the order's CRS, in the
/// georeferenced frame's units; a host-ready file is offsets from the site origin in metres, its heights
/// above the site origin's height. The survey point is the site origin as the manifest states it in
/// the projected CRS, and the site location its longitude and latitude, both verbatim (<c>HPS-33</c>).
/// Nothing is converted, projected or worked out; a value the manifest does not state is left unstated.
/// </para>
/// <para>
/// <b>No per-category levels.</b> A 3.0 download holds one level of every file (§8), so no pointer
/// carries <see cref="BundleArtifact.Levels"/>, and every step takes its file whole.
/// </para>
/// </remarks>
internal static class RevitThreeView
{
    /// <summary>The terrain's triangles, read as the TIN and, failing that, linked as CAD.</summary>
    internal const string TerrainPath = "Elevation/Terrain.dxf";

    internal const string ContoursPath = "Elevation/Contours.dxf";

    internal const string SteepGroundPath = "Elevation/Slope.gpkg";

    internal const string SiteModelPath = "Site/Site.ifc";

    internal const string RoadsPath = "Basemap/Roads.gpkg";

    internal const string PavementPath = "Basemap/Pavement.gpkg";

    internal const string WaterPath = "Basemap/Water.gpkg";

    internal const string LandUsePath = "Basemap/Landuse.gpkg";

    internal const string LandCoverPath = "Landcover/Landcover.gpkg";

    internal const string TreesPath = "Landcover/Trees.csv";

    internal const string DrapePath = "Imagery/Imagery.png";

    internal const string FloodZonesPath = "Records/Floodplain.gpkg";

    /// <summary>The frame a host-ready raster's rectangle is stated in (§9.5).</summary>
    internal const string LocalRasterFrame = "local";

    /// <summary>
    /// The file each step reads, by the kind that reads it: what the planner names when a 3.0 step's
    /// file is absent from the catalog or listed as not held.
    /// </summary>
    internal static IReadOnlyDictionary<ImportStepKind, string> FileOf { get; } = new Dictionary<ImportStepKind, string>
    {
        [ImportStepKind.ToposurfaceFromSurfaceTin] = TerrainPath,
        [ImportStepKind.ToposurfaceFromSurfaceDxf] = TerrainPath,
        [ImportStepKind.PublishedContours] = ContoursPath,
        [ImportStepKind.ContextBuildings] = SiteModelPath,
        [ImportStepKind.LinkSiteIfc] = SiteModelPath,
        [ImportStepKind.RoadCentrelines] = RoadsPath,
        [ImportStepKind.LandUse] = LandUsePath,
        [ImportStepKind.LandCover] = LandCoverPath,
        [ImportStepKind.Water] = WaterPath,
        [ImportStepKind.RoadPolygons] = PavementPath,
        [ImportStepKind.Vegetation] = TreesPath,
        [ImportStepKind.ImageryDrape] = DrapePath,
        [ImportStepKind.FloodZones] = FloodZonesPath,
        [ImportStepKind.SteepGround] = SteepGroundPath,
    };

    /// <summary>Whether <paramref name="manifest"/> is a 3.x reading, which this view and the 3.x plan apply to.</summary>
    internal static bool IsThree(BundleManifest manifest) => manifest.Contents is { Major: >= 3, Frames: not null };

    /// <summary>
    /// The 2.x-shaped manifest the planner plans <paramref name="three"/> from: the same reading, with
    /// each step's pointer filled from the catalog and the frames.
    /// </summary>
    internal static BundleManifest From(BundleManifest three)
    {
        BundleContents contents = three.Contents!;
        BundleFrames frames = contents.Frames!;
        GeoreferencedFrame georeferenced = frames.Georeferenced;
        LocalSiteFrame local = frames.Local;
        ProjectedOrigin projected = local.Origin.Projected;

        // The survey point is the site origin in the georeferenced frame's CRS (§13, R02), placed by
        // that CRS's EPSG code; a CRS named only by PROJJSON or WKT2 is not taken apart for one, so it
        // places nothing absolute and the steps that need it say so.
        int? epsg = GeoProjection.TryParseEpsg(georeferenced.HorizontalCrs.Code);
        GeoOrigin origin = new()
        {
            Lon = local.Origin.Longitude,
            Lat = local.Origin.Latitude,
            Epsg = epsg,
            Easting = projected.X,
            Northing = projected.Y,
            LinearUnit = projected.Unit.Unit,
        };

        string? groundDatum = Statement(georeferenced.VerticalCrs);
        string? localDatum = Statement(local.VerticalCrs);
        string horizontal = LinearUnits.ToManifestToken(georeferenced.HorizontalUnit.Unit);
        string vertical = LinearUnits.ToManifestToken(georeferenced.VerticalUnit.Unit);

        BundleArtifact? Georeferenced(string path, string? horizontalFrame = null, string? verticalUnits = null) =>
            Named(contents, path) is { } file
                ? new BundleArtifact
                {
                    Path = path,
                    Sha256 = file.Sha256,
                    Units = horizontal,
                    VerticalUnits = verticalUnits,
                    VerticalDatum = groundDatum,
                    HorizontalFrame = horizontalFrame,
                    Family = FileFamily.Georeferenced,
                }
                : null;

        // One GeoPackage per layer (R20): the file holds the layer alone, so its one table is read
        // whatever the file names it, the file's stem tried first.
        BundleArtifact? Layer(string path) =>
            Named(contents, path) is { } file
                ? new BundleArtifact
                {
                    Path = path,
                    Sha256 = file.Sha256,
                    Units = horizontal,
                    VerticalDatum = groundDatum,
                    Family = FileFamily.Georeferenced,
                    Table = Stem(path),
                    SoleTable = true,
                }
                : null;

        BundleArtifact? HostReady(string path) =>
            Named(contents, path) is { } file
                ? new BundleArtifact
                {
                    Path = path,
                    Sha256 = file.Sha256,
                    Units = LinearUnits.ToManifestToken(local.Unit.Unit),
                    VerticalDatum = localDatum,
                    Family = FileFamily.HostReady,
                    HeightBaseM = local.Origin.HeightMetres,
                    Raster = file.Raster,
                }
                : null;

        return new BundleManifest
        {
            IsValid = true,
            Version = three.Version,
            JobId = three.JobId,
            OrderId = three.OrderId,
            FilingKey = three.FilingKey,
            FileEntries = three.FileEntries,
            AttributionSources = three.AttributionSources,
            Contents = contents,
            SurveyPoint = origin,
            Georeference = new RevitGeoreference
            {
                CrsProjected = georeferenced.HorizontalCrs.Code ?? string.Empty,
                VerticalDatum = groundDatum ?? string.Empty,
                GridRotationDeg = local.GridRotationDegrees,
                Origin = origin,
                Declared = true,
            },
            SurfaceDxf = Georeferenced(TerrainPath, BundleManifestReader.ProjectedFrame, vertical),
            RevitContours = Georeferenced(ContoursPath, verticalUnits: vertical),
            SiteIfc = Georeferenced(SiteModelPath),
            RoadSplines = Layer(RoadsPath),
            RoadPolygons = Layer(PavementPath),
            Water = Layer(WaterPath),
            LandUse = Layer(LandUsePath),
            LandCover = Layer(LandCoverPath),
            FloodZones = Layer(FloodZonesPath),
            SteepGround = Layer(SteepGroundPath),
            TreePoints = HostReady(TreesPath),
            ImageryDrape = HostReady(DrapePath),
        };
    }

    /// <summary>The not-held entry for <paramref name="path"/>, or <c>null</c> when the manifest lists none.</summary>
    internal static SkippedFile? NotHeld(BundleManifest manifest, string path) =>
        manifest.Contents?.Skipped.FirstOrDefault(skip =>
            skip.Why == SkipCause.NotHeld && string.Equals(skip.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The file the catalog names <paramref name="path"/>, found or not: a file the catalog names and
    /// the bundle lacks still gets its pointer, so the step says the entry is missing. <c>null</c> when
    /// the catalog does not name it.
    /// </summary>
    private static BundleFile? Named(BundleContents contents, string path)
    {
        if (contents.Find(path) is { } found)
        {
            return found;
        }

        return contents.Skipped.Any(skip =>
                skip.Why == SkipCause.FileMissing && string.Equals(skip.Path, path, StringComparison.OrdinalIgnoreCase))
            ? new BundleFile(path, ManifestThreeReader.FamilyOf(path), null, null)
            : null;
    }

    /// <summary>A CRS as one statement, the form the manifest gave first: its code, else its WKT2, else its PROJJSON.</summary>
    private static string? Statement(FrameCrs crs) => crs.Code ?? crs.Wkt2 ?? crs.ProjJson;

    private static string Stem(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>A number as the release notes and skips spell it.</summary>
    internal static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
