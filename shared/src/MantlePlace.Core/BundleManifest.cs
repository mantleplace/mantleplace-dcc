namespace MantlePlace.Core;

public enum LinearUnit
{
    Unspecified,
    Metre,
    UsSurveyFoot,
    InternationalFoot,
}

public enum UnitSystem
{
    Unspecified,
    Metric,
    Imperial,
}

public sealed class BundleArtifact
{
    public required string Path { get; init; }

    public string? Sha256 { get; init; }

    public bool IsSha256Known => !string.IsNullOrWhiteSpace(Sha256);

    public string? Units { get; init; }

    public string? VerticalDatum { get; init; }

    public string? HorizontalFrame { get; init; }

    public string? Format { get; init; }

    public string? Georeference { get; init; }

    public int? TriangleCount { get; init; }

    public int? FootprintCount { get; init; }

    public string? FoliageTypeVocabulary { get; init; }

    public string? HorizontalUnits { get; init; }

    public string? VerticalUnits { get; init; }

    public bool FromOwnBlock { get; init; }

    public bool NamedByOwnBlock { get; init; }

    public string? VerticalReference { get; init; }

    public FidelityLevels? Levels { get; init; }

    public string? Table { get; init; }

    public bool IsGeoPackage => Table is not null;

    public bool SoleTable { get; init; }

    public BundleArtifact? GeoJsonFallback { get; init; }

    internal FileFamily? Family { get; init; }

    internal double? HeightBaseM { get; init; }

    internal RasterGrid? Raster { get; init; }
}

public sealed class ReadinessPath
{
    public bool Present { get; init; }

    public string Reason { get; init; } = string.Empty;

    public bool Declared { get; init; }
}

public sealed class RevitReadiness
{
    public ReadinessPath ToposurfacePoints { get; init; } = new();

    public ReadinessPath IfcSite { get; init; } = new();

    public ReadinessPath SurfaceDxf { get; init; } = new();

    public ReadinessPath Vectors { get; init; } = new();

    public ReadinessPath Contours { get; init; } = new();

    public ReadinessPath FloodZones { get; init; } = new();

    public ReadinessPath SteepGround { get; init; } = new();

    public bool Declared { get; init; }
}

public enum FileFrameKind
{
    Unknown,

    Projected,

    Local,
}

public sealed class FileFrame
{
    public FileFrameKind Kind { get; init; }

    public int? Epsg { get; init; }

    public GeoOrigin? Origin { get; init; }

    public LinearUnit? HorizontalUnit { get; init; }

    public LinearUnit? VerticalUnit { get; init; }
}

public sealed class GeoOrigin
{
    public double? Lon { get; init; }

    public double? Lat { get; init; }

    public int? Epsg { get; init; }

    public double? Easting { get; init; }

    public double? Northing { get; init; }

    public LinearUnit LinearUnit { get; init; }

    public bool IsUsable => Easting.HasValue && Northing.HasValue && Epsg.HasValue;
}

public sealed class RevitGeoreference
{
    public string CrsProjected { get; init; } = string.Empty;

    public string VerticalDatum { get; init; } = string.Empty;

    public double? GridRotationDeg { get; init; }

    public GeoOrigin? Origin { get; init; }

    public bool Declared { get; init; }
}

public sealed class GroundExtent
{
    public required double Left { get; init; }

    public required double Bottom { get; init; }

    public required double Right { get; init; }

    public required double Top { get; init; }

    public int Epsg { get; init; }

    public bool IsUsable => Epsg != 0 && Right > Left && Top > Bottom;

    public double WidthUnits => Right - Left;

    public double HeightUnits => Top - Bottom;
}

public sealed record AttributionSource(
    string ProviderId,
    string? AttributionText,
    string? License,
    string? LicenseUrl);

public sealed record FloodPanel(string Panel, string? EffectiveDate);

public sealed class FloodMap
{
    public IReadOnlyList<string> Zones { get; init; } = [];

    public IReadOnlyList<string> DfirmIds { get; init; } = [];

    public IReadOnlyList<FloodPanel> Panels { get; init; } = [];

    public string Source { get; init; } = string.Empty;
}

public sealed class PublishedTimeZone
{
    public string Iana { get; init; } = string.Empty;

    public required double UtcOffsetStandardH { get; init; }

    public bool? ObservesDst { get; init; }
}

public sealed class DeliveryFacts
{
    public UnitSystem UnitSystem { get; init; }

    public string UnitSystemValue { get; init; } = string.Empty;

    public string Tier { get; init; } = string.Empty;

    public LinearUnit LinearUnit { get; init; }

    public int? HorizontalEpsg { get; init; }

    public string? Label { get; init; }

    public GeoOrigin? LocalOrigin { get; init; }

    public bool Declared { get; init; }
}

public sealed class BundleManifest
{
    public bool IsValid { get; internal set; }

    public string Error { get; internal set; } = string.Empty;

    public string JobId { get; internal set; } = string.Empty;

    public string OrderId { get; internal set; } = string.Empty;

    public string FilingKey { get; internal set; } = string.Empty;

    public string Version { get; internal set; } = string.Empty;

    public string DeliveryModel { get; internal set; } = string.Empty;

    public double BboxWestDeg { get; internal set; }

    public double BboxSouthDeg { get; internal set; }

    public double BboxEastDeg { get; internal set; }

    public double BboxNorthDeg { get; internal set; }

    public bool HasBbox { get; internal set; }

    public string CesiumTerrainPath { get; internal set; } = string.Empty;

    public IReadOnlyDictionary<string, string> Layout { get; internal set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> ProjectedVariants { get; internal set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string RoadSplinesPath { get; internal set; } = string.Empty;

    public string RoadSplinesSha256 { get; internal set; } = string.Empty;

    public bool HasRoadSplines { get; internal set; }

    public BundleArtifact? RoadSplines { get; internal set; }

    public BundleArtifact? LandUse { get; internal set; }

    public BundleArtifact? LandCover { get; internal set; }

    public BundleArtifact? Water { get; internal set; }

    public BundleArtifact? RoadPolygons { get; internal set; }

    public bool VectorsFromOwnBlock { get; internal set; }

    public IReadOnlySet<string> SharedLayersWithNothingToPlace { get; internal set; } = new HashSet<string>(StringComparer.Ordinal);

    public BundleArtifact? FloodZones { get; internal set; }

    public BundleArtifact? SteepGround { get; internal set; }

    public FloodMap? FloodMap { get; internal set; }

    public string? SteepGroundThreshold { get; internal set; }

    public bool HasSteepGroundBlock { get; internal set; }

    public FileFrame? FileFrame { get; internal set; }

    public bool HasRoadLayer { get; internal set; }

    public BundleArtifact? TreePoints { get; internal set; }

    public IReadOnlyList<AttributionSource> AttributionSources { get; internal set; } = [];

    public IReadOnlyDictionary<string, FileLicensing> FileEntries { get; internal set; }
        = new Dictionary<string, FileLicensing>();

    public DeliveryFacts Delivery { get; internal set; } = new();

    public PublishedTimeZone? TimeZone { get; internal set; }

    public RevitReadiness Readiness { get; internal set; } = new();

    public RevitGeoreference Georeference { get; internal set; } = new();

    public BundleArtifact? ToposurfacePoints { get; internal set; }

    public BundleArtifact? SurfaceDxf { get; internal set; }

    public BundleArtifact? SiteIfc { get; internal set; }

    public BundleArtifact? LandXml { get; internal set; }

    public BundleArtifact? ContoursDxf { get; internal set; }

    public BundleArtifact? ImageryDrape { get; internal set; }

    public GroundExtent? ImageryDrapeExtent { get; internal set; }

    public GroundExtent? DemBounds { get; internal set; }

    public BundleArtifact? RevitDrape { get; internal set; }

    public GroundExtent? RevitDrapeExtent { get; internal set; }

    public BundleArtifact? RevitContours { get; internal set; }

    public double? ImageryGsdM { get; internal set; }

    public bool ImageryAbsentByDeclaration { get; internal set; }

    public bool HasRevitContent =>
        ToposurfacePoints is not null || SurfaceDxf is not null || SiteIfc is not null;

    public GeoOrigin? SurveyPoint { get; internal set; }

    public int? SiteOriginEpsg { get; internal set; }

    public string? SiteOriginElsewhere(string outcome)
        => SiteOriginEpsg is { } origin
            && GeoProjection.TryParseEpsg(Georeference.CrsProjected) is { } delivery
            && origin != delivery
            ? $"{outcome}: this bundle's files are in EPSG:{delivery}, and it states its site origin only in EPSG:{origin}, "
                + "a different coordinate system. The format promises the origin in the files' own system, and "
                + "placing one in the other would mean reprojecting, which this plugin never does."
            : null;

    public bool HasPreDerivedSurveyPoint => SurveyPoint is { IsUsable: true };

    public BundleContents? Contents { get; internal set; }

    public static string DeriveCesiumTerrainPrefix(string cesiumTerrainPath)
    {
        if (string.IsNullOrEmpty(cesiumTerrainPath))
        {
            return string.Empty;
        }

        int cut = cesiumTerrainPath.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? string.Empty : cesiumTerrainPath[..(cut + 1)];
    }
}
