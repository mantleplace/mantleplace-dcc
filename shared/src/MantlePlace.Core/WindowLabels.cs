using System.Globalization;

namespace MantlePlace.Core;

public static partial class WindowLabels
{
    public const string VaultWindowTitle = "Mantle Place Vault";

    public const string SignInWindowTitle = "Mantle Place Sign In";

    public const string VaultHeading = "Vault";

    public const string SignInHeading = "Sign In";

    public const string Refresh = "Refresh";

    public const string PrepareForRevit = "Prepare for Revit";

    public const string Import = "Import";

    public const string RemoveDownload = "Remove Download";

    public const string ReportProblem = "Report a Problem";

    public const string Cancel = "Cancel";

    public const string Close = "Close";

    public const string Search = "Search";

    public const string SearchToolTip = "Shows only the bundles whose area label contains this text.";

    public const string AreaUnknown = "Area Unknown";

    public const string SizeUnknown = "Size Unknown";

    public const string ManifestVersionUnknown = "Manifest Version Unknown";

    public const string ImportWindowTitle = "Mantle Place Bundle Import";

    public const string ImportHeading = "Bundle Import";

    public static string StateWord(ImportStepState state) => state switch
    {
        ImportStepState.Waiting => "Waiting",
        ImportStepState.Importing => "Importing",
        ImportStepState.Done => "Done",
        ImportStepState.Failed => "Failed",
        ImportStepState.Cancelled => "Cancelled",
        ImportStepState.NotRun => "Not Run",
        _ => state.ToString(),
    };

    public static string StepName(ImportStepKind kind) => kind switch
    {
        ImportStepKind.SetSharedCoordinates => "Shared Coordinates",
        ImportStepKind.SetSiteLocation => "Site Location",
        ImportStepKind.SiteContextView => "Site Context View",
        ImportStepKind.AttributionAndProvenance => "Attribution",
        _ when ImportLayers.Of(kind) is { } layer => LayerName(layer),

        _ => kind.ToString(),
    };

    public static string LayerName(ImportLayer layer) => KnownLayerName(layer) ?? layer.ToString();

    public static string? KnownLayerName(ImportLayer layer) => layer switch
    {
        ImportLayer.Terrain => "Terrain",
        ImportLayer.PublishedContours => "Published Contours",
        ImportLayer.ContextBuildings => "Context Buildings",
        ImportLayer.SiteModel => "Site Model",
        ImportLayer.RoadCentrelines => "Road Centrelines",
        ImportLayer.LandUsePlan => "Land Use Plan",
        ImportLayer.LandCoverPlan => "Land Cover Plan",
        ImportLayer.WaterSubdivisions => "Water Subdivisions",
        ImportLayer.RoadSubdivisions => "Road Subdivisions",
        ImportLayer.Planting => "Planting",
        ImportLayer.FloodZones => "Flood Zones",
        ImportLayer.SteepGround => "Steep Ground",
        ImportLayer.ImageryDrape => "Imagery Drape",
        _ => null,
    };

    public const string IncludeHeading = "Include";

    public static string Needs(ImportLayer prerequisite) => $"Needs {LayerName(prerequisite)}";

    public const string UnavailableHeading = "Unavailable";

    public static string? UnavailableReason(SkipReasonCode code, int count)
    {
        bool many = count > 1;
        string these = many ? "these" : "this";
        string them = many ? "them" : "it";

        return code switch
        {
            SkipReasonCode.NoSiteFrame or SkipReasonCode.CoordinateSystemNotSupported or SkipReasonCode.PredatesHostCopy =>
                $"This bundle was built before Revit could receive {these}. Download the bundle again from your vault to get {them}.",
            SkipReasonCode.ArtifactNotInManifest =>
                $"This bundle does not carry {these}. Add {them} to the order in your vault, then download the bundle again.",
            SkipReasonCode.EntryNotInArchive =>
                $"This bundle is missing the {(many ? "files" : "file")} for {these}. Download the bundle again from your vault to get {them}.",
            SkipReasonCode.UnitNotUnderstood =>
                $"This bundle measures {these} in a unit this version of Mantle Place cannot read. Update Mantle Place to import {them}.",

            SkipReasonCode.ExtentNotCorroborated =>
                $"Mantle Place could not confirm which ground {these} {(many ? "cover" : "covers")}. Download the bundle again from your vault to get {them}.",

            SkipReasonCode.DeclaredAbsent
                or SkipReasonCode.DerivedLayerNotPublished
                or SkipReasonCode.SupersededByFallback
                or SkipReasonCode.FallbackSuppressed
                or SkipReasonCode.NoSurveyPoint
                or SkipReasonCode.NoGeographicOrigin
                or SkipReasonCode.GeographicOriginOutOfRange
                or SkipReasonCode.LeftOutByChoice
                or SkipReasonCode.LevelUnavailable

                or SkipReasonCode.NotInFormat => null,

            SkipReasonCode.NotHeld =>
                $"This bundle does not hold {these}. The import log gives the bundle's reason.",

            _ => null,
        };
    }

    public static string ProgressText(StepProgress progress)
        => string.Format(CultureInfo.InvariantCulture, "{0:N0} of {1:N0}", progress.Done, progress.Total);

    public static string IsCommitting(string hostName) => $"{hostName} is committing";

    public const string Finishing = "Finishing";

    public static string Elapsed(TimeSpan elapsed)
    {
        long seconds = Math.Max(0L, (long)elapsed.TotalSeconds);
        return seconds switch
        {
            < 60 => string.Format(CultureInfo.InvariantCulture, "{0} s", seconds),
            < 3_600 => string.Format(CultureInfo.InvariantCulture, "{0} min {1} s", seconds / 60, seconds % 60),
            _ => string.Format(CultureInfo.InvariantCulture, "{0} h {1} min", seconds / 3_600, seconds % 3_600 / 60),
        };
    }

    public static string StatusLine(ImportRunView view, TimeSpan elapsed)
        => StatusLine(view, elapsed, HostName(HostIdentity.Id), estimate: null);

    public static string StatusLine(ImportRunView view, TimeSpan elapsed, string hostName, TimeSpan? estimate)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrEmpty(hostName);

        string name;
        if (view.CurrentName is { } current)
        {
            name = current;
        }
        else if (view.Finishing)
        {
            name = Finishing;
        }
        else
        {
            return string.Empty;
        }

        List<string> parts = [];
        if (view.Progress is { Total: > 0 } progress)
        {
            parts.Add(ProgressText(progress));
        }

        if (view.Committing)
        {
            parts.Add(IsCommitting(hostName));
        }

        parts.Add(estimate is { } measured && view.Current is not null
            ? $"{Elapsed(elapsed)} of about {Elapsed(measured)}"
            : Elapsed(elapsed));
        string line = $"{name}: {string.Join(", ", parts)}";

        return view.CancelRequested && view.Current is not null
            ? line + ". Cancelling at the next step or chunk."
            : line;
    }
}
