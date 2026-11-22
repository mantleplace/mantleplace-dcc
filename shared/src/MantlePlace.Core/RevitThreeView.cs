using System.Globalization;

namespace MantlePlace.Core;

public sealed record ThreeLeftOutItem(ImportStepKind? Kind, string Item, string Why);

internal static class RevitThreeView
{
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

    internal const string LocalRasterFrame = "local";

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

    internal static bool IsThree(BundleManifest manifest) => manifest.Contents is { Major: >= 3, Frames: not null };

    internal static BundleManifest From(BundleManifest three)
    {
        BundleContents contents = three.Contents!;
        BundleFrames frames = contents.Frames!;
        GeoreferencedFrame georeferenced = frames.Georeferenced;
        LocalSiteFrame local = frames.Local;
        ProjectedOrigin projected = local.Origin.Projected;

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

        string? groundDatum = HeightDatums.FromFrame(georeferenced.VerticalCrs, georeferenced.GeoidModel);
        string? localDatum = HeightDatums.FromFrame(local.VerticalCrs, local.GeoidModel);
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

        BundleArtifact? Layer(string path, SiteVectorLayer layer) =>
            Named(contents, path) is { } file
                ? new BundleArtifact
                {
                    Path = path,
                    Sha256 = file.Sha256,
                    Units = horizontal,
                    VerticalDatum = groundDatum,
                    Family = FileFamily.Georeferenced,
                    Table = TableOf(file, path, layer),
                    SoleTable = file.Tables.Count == 0,
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
                    FoliageTypeVocabulary = file.FoliageTypeVocabulary,
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
            RoadSplines = Layer(RoadsPath, SiteVectorLayers.RoadSplines),
            RoadPolygons = Layer(PavementPath, SiteVectorLayers.RoadPolygons),
            Water = Layer(WaterPath, SiteVectorLayers.Water),
            LandUse = Layer(LandUsePath, SiteVectorLayers.LandUse),
            LandCover = Layer(LandCoverPath, SiteVectorLayers.LandCover),
            FloodZones = Layer(FloodZonesPath, SiteVectorLayers.FloodZones),
            SteepGround = Layer(SteepGroundPath, SiteVectorLayers.SteepGround),
            TreePoints = HostReady(TreesPath),
            ImageryDrape = HostReady(DrapePath),
        };
    }

    internal static SkippedFile? NotHeld(BundleManifest manifest, string path) =>
        manifest.Contents?.Skipped.FirstOrDefault(skip =>
            skip.Why == SkipCause.NotHeld && string.Equals(skip.Path, path, StringComparison.OrdinalIgnoreCase));

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

    internal static string TableOf(BundleFile file, string path, SiteVectorLayer layer)
    {
        string stem = Stem(path);
        IReadOnlyList<string> tables = file.Tables;
        if (tables.Count == 0)
        {
            return stem;
        }

        string family = layer.DrawnFrom == SiteGeometryKinds.Lines ? "_line" : "_polygon";
        return tables.FirstOrDefault(name => string.Equals(name, stem, StringComparison.OrdinalIgnoreCase))
            ?? (tables.Count == 1 ? tables[0] : null)
            ?? tables.FirstOrDefault(name => name.EndsWith(family, StringComparison.OrdinalIgnoreCase))
            ?? stem;
    }

    private static string Stem(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    internal static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
