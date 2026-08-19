namespace MantlePlace.Revit.Core;

/// <summary>What the add-in will do to the Revit document, in order.</summary>
public enum ImportStepKind
{
    /// <summary>Massing &amp; Site ▸ Toposurface ▸ Create from Import ▸ Specify Points File.</summary>
    ToposurfaceFromPointsFile,

    /// <summary>Insert ▸ Link CAD, then Create from Import ▸ Select Import Instance.</summary>
    ToposurfaceFromSurfaceDxf,

    /// <summary>Insert ▸ Link IFC — kept as a coordinated reference, not opened as a model.</summary>
    LinkSiteIfc,

    /// <summary>Publish the pre-derived survey point / shared coordinates.</summary>
    SetSharedCoordinates,

    /// <summary>
    /// Road centrelines from the <c>road_splines</c> vector layer — Forma's "Roads" row.
    /// </summary>
    RoadCentrelines,

    /// <summary>
    /// Property boundaries from the <c>land_use</c> vector layer — Forma's "Site limits" row.
    /// </summary>
    SiteBoundaries,

    /// <summary>
    /// Trees from the tree-points file, with real height and crown — Forma's "Vegetation" row.
    /// </summary>
    Vegetation,

    /// <summary>
    /// The satellite imagery draped on the terrain as a material texture — Forma's last row.
    /// </summary>
    ImageryDrape,
}

/// <summary>Why a step the bundle might have carried is not in the plan, as a closed vocabulary.</summary>
/// <remarks>
/// The machine-readable half of a skip. <see cref="SkippedImport.Reason"/> is prose written for a
/// curator and is expected to be reworded; a test, a UI branch or a support triage rule that needs
/// to know WHICH skip happened reads this instead of matching on the sentence.
/// </remarks>
public enum SkipReasonCode
{
    /// <summary>The manifest carries no pointer for this artifact.</summary>
    ArtifactNotInManifest,

    /// <summary>The manifest points at an entry the archive does not contain.</summary>
    EntryNotInArchive,

    /// <summary>The artifact declares a linear unit this host cannot read, so it fails closed.</summary>
    UnitNotUnderstood,

    /// <summary>A lower-precedence path was planned in this one's place.</summary>
    SupersededByFallback,

    /// <summary>This path was the fallback, and it was deliberately not attempted.</summary>
    FallbackSuppressed,

    /// <summary>The manifest carries no pre-derived survey point to publish.</summary>
    NoSurveyPoint,

    /// <summary>
    /// The artifact's coordinates are absolute, and the manifest published no origin to place them
    /// against.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NoSurveyPoint"/>, which is about a step that does not happen. This
    /// one is about geometry that cannot be positioned: the toposurface points file is already local
    /// and imports fine without an origin, while a tree at easting 472 195 has nowhere to go.
    /// </remarks>
    NoSiteFrame,

    /// <summary>
    /// The artifact's coordinate system cannot be brought into the bundle's own origin frame by the
    /// one projection this host is permitted to perform (<c>HPS-45</c>).
    /// </summary>
    CoordinateSystemNotSupported,

    /// <summary>
    /// The ground extent an artifact would be stretched over could not be corroborated against the
    /// artifact's own contents, so it was not used.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ArtifactNotInManifest"/> and from
    /// <see cref="CoordinateSystemNotSupported"/>, and the distinction is the whole point: those two
    /// mean the manifest said nothing, or said something this host cannot place. This one means the
    /// manifest said something PLAUSIBLE that the bytes did not back up. A drape hung on an extent
    /// that is wrong looks like a correct import of a photograph of somewhere else, which is the one
    /// failure a curator has no way to notice.
    /// </remarks>
    ExtentNotCorroborated,
}

/// <summary>
/// Where a draped texture's image is pinned to the ground, decided here and not in the shim.
/// </summary>
/// <remarks>
/// <para>
/// The four edges arrive as an absolute AOI-UTM rectangle and leave as frame-local metres, through
/// the same <see cref="SiteFrame"/> every other absolutely-positioned artifact goes through — so the
/// drape inherits its refusals rather than restating them, and the shim computes no rectangle of its
/// own (<c>HPS-02</c>, <c>HPS-33</c>).
/// </para>
/// <para>
/// Metres, not feet. The unit conversion Revit's API demands stays in the shim, exactly as it does
/// for <see cref="SurveyPointPlacement"/> — this type states where the image goes, and Revit's
/// preference for decimal feet is Revit's business.
/// </para>
/// </remarks>
public sealed class DrapePlacement
{
    /// <summary>West edge, in frame-local metres.</summary>
    public required double LeftM { get; init; }

    /// <summary>South edge, in frame-local metres.</summary>
    public required double BottomM { get; init; }

    /// <summary>East edge, in frame-local metres.</summary>
    public required double RightM { get; init; }

    /// <summary>North edge, in frame-local metres.</summary>
    public required double TopM { get; init; }

    /// <summary>The image's own pixel grid, read from its header and checked to be non-degenerate.</summary>
    public required ImageSize PixelSize { get; init; }

    /// <summary>
    /// True when the extent came from the drape's own host-neutral block rather than from the DEM's
    /// bounds.
    /// </summary>
    /// <remarks>
    /// Carried so the import log can say which pointer placed the image. The two paths are not
    /// equally trustworthy — one is a contract and the other is a corroborated inference — and a
    /// tester reading a log after the fact cannot tell them apart from the numbers alone.
    /// </remarks>
    public required bool ExtentFromDrapeBlock { get; init; }

    /// <summary>Real-world width the image spans, in metres.</summary>
    public double WidthM => RightM - LeftM;

    /// <summary>Real-world height the image spans, in metres.</summary>
    public double HeightM => TopM - BottomM;
}

/// <summary>Why a step the bundle might have carried is not in the plan.</summary>
public sealed class SkippedImport
{
    public required ImportStepKind Kind { get; init; }

    /// <summary>Which skip this is, for anything that needs to branch rather than print.</summary>
    public required SkipReasonCode ReasonCode { get; init; }

    /// <summary>
    /// User-facing sentence. Where the manifest states a reason of its own
    /// (<c>dcc_readiness.revit.&lt;path&gt;.reason</c>) that reason is surfaced rather than replaced —
    /// dead-ending on an empty import is the failure this rule exists to prevent (HPS-36).
    /// </summary>
    /// <remarks>
    /// Surfaced, not echoed. The manifest's token is translated by
    /// <see cref="ReadinessReasons.ClauseFor"/> before it reaches this string: that field's
    /// vocabulary is open until v19 and includes <c>emit_threw:&lt;stage&gt;</c>, so interpolating it
    /// verbatim put internal stage identifiers in front of curators.
    /// </remarks>
    public required string Reason { get; init; }
}

/// <summary>
/// Everything Revit's <c>ProjectPosition</c> needs, decided here rather than in the shim.
/// </summary>
/// <remarks>
/// <para>
/// The shim used to pass <c>0.0, 0.0</c> for elevation and angle. Both happened to be right for the
/// bundles on hand, and neither was derived from anything — so the day a bundle published a rotated
/// grid the model would have been placed wrong with no test to notice. Deriving them here puts the
/// rule where the headless suite can reach it (HPS-02): the shim is left with the unit conversion
/// Revit's API demands and nothing else.
/// </para>
/// </remarks>
public sealed class SurveyPointPlacement
{
    /// <summary>The pre-derived origin, applied verbatim (HPS-33).</summary>
    public required GeoOrigin Origin { get; init; }

    /// <summary>
    /// <c>revit.georeference.grid_rotation_deg</c> in radians, which is what Revit's
    /// <c>ProjectPosition</c> takes.
    /// </summary>
    public required double AngleRadians { get; init; }

    /// <summary>
    /// The survey point's elevation. Always zero, and zero for a stated reason.
    /// </summary>
    /// <remarks>
    /// The manifest's <c>revit.units_note</c> states the contract: apply the origin verbatim as the
    /// survey point and <em>set its Elev to 0</em>, because every artifact's Z is ABSOLUTE
    /// orthometric height rather than an offset from the origin. A non-zero survey-point elevation
    /// would therefore double-count it. The v19 origin block publishes no height of its own; if it
    /// ever does, this is the single place that has to change.
    /// </remarks>
    public double ElevationM => 0.0;
}

/// <summary>One resolved action, with its bundle entry already checked to exist.</summary>
public sealed class ImportStep
{
    public required ImportStepKind Kind { get; init; }

    /// <summary>The entry name inside the bundle zip, exactly as the archive spells it.</summary>
    public string EntryName { get; init; } = string.Empty;

    /// <summary>The unit the artifact's coordinates are in — what Revit's import dialog needs.</summary>
    public LinearUnit Units { get; init; } = LinearUnit.Unspecified;

    /// <summary>
    /// The manifest's sha256 for <see cref="EntryName"/>, or <c>null</c> when it advertised none.
    /// </summary>
    /// <remarks>
    /// Carried on the step so the integrity check cannot be skipped by an extraction that never
    /// looked the hash up. <c>null</c> is <em>unknown</em>, which is a v18 bundle and a skip — never
    /// "corrupt", and never "verified" (HPS-27, ⛔HPS-28).
    /// </remarks>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Populated only for <see cref="ImportStepKind.SetSharedCoordinates"/>.</summary>
    public SurveyPointPlacement? SurveyPoint { get; init; }

    /// <summary>
    /// The frame this step's geometry is placed in, for the kinds whose artifact does not arrive
    /// already local.
    /// </summary>
    /// <remarks>
    /// Carried on the step rather than looked up by the shim, and only ever non-<c>null</c> where
    /// the planner has already checked the frame CAN place that artifact. That check is the
    /// interesting part — a foot-tier origin cannot place a metric UTM layer, and the failure is a
    /// site quietly built two thousand kilometres away — so it belongs where a headless test reaches
    /// it (<c>HPS-02</c>).
    /// </remarks>
    public SiteFrame? Frame { get; init; }

    /// <summary>Populated only for <see cref="ImportStepKind.ImageryDrape"/>.</summary>
    public DrapePlacement? Drape { get; init; }
}

/// <summary>
/// The pure decision about what to import from a bundle, and what to tell the user about the rest.
/// </summary>
/// <remarks>
/// Produced by <see cref="BundleImportPlanner"/> from a manifest plus the archive's entry list —
/// no Revit, no file system, no zip reader. The add-in shim executes it. That split is what makes
/// the import policy — which topo path wins, what happens when the points file is missing, whether
/// shared coordinates get set — assertable in a headless test (HPS-02, HPS-42).
/// </remarks>
public sealed class BundleImportPlan
{
    public required bool CanImport { get; init; }

    public IReadOnlyList<ImportStep> Steps { get; init; } = [];

    public IReadOnlyList<SkippedImport> Skipped { get; init; } = [];

    /// <summary>
    /// Set when the bundle carries artifacts this v1 does not import — the LandXML (a Civil 3D
    /// deliverable) and the 2-D contour linework. Listed so the UI can say they are in the zip
    /// rather than leaving the user to wonder.
    /// </summary>
    public IReadOnlyList<string> AvailableButNotImported { get; init; } = [];

    /// <summary>Why nothing can be imported, when <see cref="CanImport"/> is false.</summary>
    public string BlockedReason { get; init; } = string.Empty;
}
