using System.Globalization;

namespace MantlePlace.Core;

public enum ImportStepKind
{
    ToposurfaceFromPointsFile,

    ToposurfaceFromSurfaceTin,

    ToposurfaceFromSurfaceDxf,

    PublishedContours,

    LinkSiteIfc,

    ContextBuildings,

    SetSharedCoordinates,

    SetSiteLocation,

    RoadCentrelines,

    LandUse,

    LandCover,

    Water,

    RoadPolygons,

    Vegetation,

    SiteContextView,

    ImageryDrape,

    AttributionAndProvenance,

    FloodZones,

    SteepGround,
}

public enum TerrainToposolidType
{
    Project,

    Imagery,
}

public enum SkipReasonCode
{
    ArtifactNotInManifest,

    EntryNotInArchive,

    UnitNotUnderstood,

    SupersededByFallback,

    FallbackSuppressed,

    NoSurveyPoint,

    NoSiteFrame,

    CoordinateSystemNotSupported,

    ExtentNotCorroborated,

    NoGeographicOrigin,

    GeographicOriginOutOfRange,
    DerivedLayerNotPublished,

    LeftOutByChoice,

    DeclaredAbsent,

    PredatesHostCopy,

    LevelUnavailable,

    NotInFormat,

    NotHeld,
}

public sealed class DrapePlacement
{
    public required double LeftM { get; init; }

    public required double BottomM { get; init; }

    public required double RightM { get; init; }

    public required double TopM { get; init; }

    public required ImageSize PixelSize { get; init; }

    public required bool ExtentFromDrapeBlock { get; init; }

    public double WidthM => RightM - LeftM;

    public double HeightM => TopM - BottomM;

    public DrapePlacement Shifted(double eastM, double northM) => new()
    {
        LeftM = LeftM + eastM,
        BottomM = BottomM + northM,
        RightM = RightM + eastM,
        TopM = TopM + northM,
        PixelSize = PixelSize,
        ExtentFromDrapeBlock = ExtentFromDrapeBlock,
    };
}

public sealed class SkippedImport
{
    public required ImportStepKind Kind { get; init; }

    public required SkipReasonCode ReasonCode { get; init; }

    public required string Reason { get; init; }
}

public sealed class SurveyPointPlacement
{
    public required GeoOrigin Origin { get; init; }

    public required double AngleRadians { get; init; }

    public double ElevationM => 0.0;
}

public sealed class SiteLocationPlacement
{
    public required double LatitudeDeg { get; init; }

    public required double LongitudeDeg { get; init; }

    public double LatitudeRadians => LatitudeDeg * Math.PI / 180.0;

    public double LongitudeRadians => LongitudeDeg * Math.PI / 180.0;

    public SiteTimeZone? TimeZone { get; init; }

    public double TimeZoneToWrite(double projectHours) => TimeZone?.RevitHours ?? projectHours;

    public string LogLine(double projectHours)
    {
        string placed = string.Format(
            CultureInfo.InvariantCulture,
            "Set the site location from the manifest: latitude {0}°, longitude {1}°. ",
            LatitudeDeg,
            LongitudeDeg);

        return placed + (TimeZone?.Describe(projectHours) ?? string.Format(
            CultureInfo.InvariantCulture,
            "The bundle publishes no time zone, so the project keeps its own, {0} — set the site's under "
            + "Manage ▸ Location before a sun study that depends on the clock.",
            SiteTimeZone.FormatUtc(projectHours)));
    }
}

public sealed class SiteTimeZone
{
    public const double RevitLimitHours = 12.0;

    public required PublishedTimeZone Published { get; init; }

    public double? RevitHours => Wrap(Published.UtcOffsetStandardH);

    public bool IsWrapped => RevitHours is { } hours && hours != Published.UtcOffsetStandardH;

    public string Describe(double projectHours)
    {
        double published = Published.UtcOffsetStandardH;
        if (RevitHours is not { } written)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "The bundle publishes a time zone offset of {0} hours, which is not a time zone, so the "
                + "project keeps its own, {1}.",
                published,
                FormatUtc(projectHours));
        }

        string zone = IsWrapped
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0} is {1}, and Revit takes only UTC-12 to UTC+12, so the site's time zone is {2}: the "
                + "same clock time a calendar day {3}, which moves the sun by less than half a degree.",
                Published.Iana.Length > 0 ? Published.Iana : "The published zone",
                FormatUtc(published),
                FormatUtc(written),
                published > 0.0 ? "later" : "earlier")
            : string.Format(
                CultureInfo.InvariantCulture,
                "Time zone {0}{1}, as published.",
                FormatUtc(written),
                Published.Iana.Length > 0 ? $" ({Published.Iana})" : string.Empty);

        return zone + Published.ObservesDst switch
        {
            true => " The zone observes daylight saving time, and a plugin cannot switch Revit's on: tick the "
                + "daylight saving box under Manage ▸ Location before a sun study in the months it applies.",
            false => " The zone does not observe daylight saving time: if the daylight saving box under "
                + "Manage ▸ Location is ticked, untick it.",
            null => string.Empty,
        };
    }

    internal static double? Wrap(double hours)
    {
        if (!(Math.Abs(hours) < 24.0))
        {
            return null;
        }

        return hours > RevitLimitHours ? hours - 24.0
            : hours < -RevitLimitHours ? hours + 24.0
            : hours;
    }

    public static string FormatUtc(double hours)
    {
        int minutes = (int)Math.Round(Math.Abs(hours) * 60.0, MidpointRounding.AwayFromZero);
        string sign = hours < 0.0 && minutes > 0 ? "-" : "+";
        return minutes % 60 == 0
            ? string.Format(CultureInfo.InvariantCulture, "UTC{0}{1}", sign, minutes / 60)
            : string.Format(CultureInfo.InvariantCulture, "UTC{0}{1}:{2:00}", sign, minutes / 60, minutes % 60);
    }
}

public sealed class ImportStep
{
    public required ImportStepKind Kind { get; init; }

    public string EntryName { get; init; } = string.Empty;

    public LinearUnit Units { get; init; } = LinearUnit.Unspecified;

    public LinearUnit VerticalUnits { get; init; } = LinearUnit.Unspecified;

    public LayerFrame? Layer { get; init; }

    public string? ExpectedSha256 { get; init; }

    public bool SavesLinkCompanion { get; init; }

    public ExtractionLifetime Lifetime => SavesLinkCompanion
        ? ImportStepKinds.LifetimeOf(ImportStepKind.LinkSiteIfc)
        : ImportStepKinds.LifetimeOf(Kind);

    public SurveyPointPlacement? SurveyPoint { get; init; }

    public SiteLocationPlacement? SiteLocation { get; init; }

    public SiteFrame? Frame { get; init; }

    public DrapePlacement? Drape { get; init; }

    public string? FoliageTypeVocabulary { get; init; }

    public SurfaceCropWindow? Crop { get; init; }

    public string? CropUnavailable { get; init; }

    public TerrainToposolidType ToposolidType { get; init; } = TerrainToposolidType.Project;

    public bool DrapePlanned { get; init; }

    public ProjectProvenance? Provenance { get; init; }

    public HazardPlanFacts? Hazard { get; init; }

    public LandPlanFacts? LandPlan { get; init; }

    public StatedDatum? HeightDatum { get; init; }

    public double? HeightBaseM { get; init; }

    public FidelityLevels? Levels { get; init; }

    public CostDriver? Cost { get; internal set; }

    public AppliedLevel? Level { get; init; }

    public IReadOnlyList<string> LevelFiles { get; init; } = [];

    public string? Table { get; init; }

    public bool SoleTable { get; init; }

    public ImportStep? GeoJsonFallback { get; init; }

    public OdblRole Odbl { get; internal set; } = OdblRole.None;
}

public sealed class HazardPlanFacts
{
    public required string Build { get; init; }

    public FootprintExtent? Crop { get; init; }

    public FloodMap? FloodMap { get; init; }

    public string? Threshold { get; init; }
}

public sealed class LandPlanFacts
{
    public required string Build { get; init; }

    public FootprintExtent? Crop { get; init; }
}

public sealed class BundleImportPlan
{
    public required bool CanImport { get; init; }

    public IReadOnlyList<ImportStep> Steps { get; init; } = [];

    public IReadOnlyList<SkippedImport> Skipped { get; init; } = [];

    public IReadOnlyList<string> AvailableButNotImported { get; init; } = [];

    public string BlockedReason { get; init; } = string.Empty;
}
