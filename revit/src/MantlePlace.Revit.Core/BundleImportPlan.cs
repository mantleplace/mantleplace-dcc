using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>What the add-in will do to the Revit document, in order.</summary>
public enum ImportStepKind
{
    /// <summary>Massing &amp; Site ▸ Toposurface ▸ Create from Import ▸ Specify Points File.</summary>
    ToposurfaceFromPointsFile,

    /// <summary>
    /// The toposolid built from the surface DXF's own TIN vertices — the preferred topo path.
    /// </summary>
    /// <remarks>
    /// Same terrain as <see cref="ToposurfaceFromPointsFile"/> and fewer vertices, but sampled
    /// adaptively instead of on a lattice. The points file is a perfectly regular grid, which makes
    /// every cell's four corners cocircular and Delaunay triangulation degenerate, so the
    /// triangulator picks slivers and fans arbitrarily and the terrain reads as faceted however good
    /// the drape is. TIN vertices sit in general position and triangulate cleanly.
    /// </remarks>
    ToposurfaceFromSurfaceTin,

    /// <summary>Insert ▸ Link CAD, then Create from Import ▸ Select Import Instance.</summary>
    ToposurfaceFromSurfaceDxf,

    /// <summary>Insert ▸ Link IFC — kept as a coordinated reference, not opened as a model.</summary>
    /// <remarks>
    /// Its checklist row starts unchecked (<see cref="ImportLayers.OnByDefault"/>):
    /// <see cref="ContextBuildings"/> puts the same buildings in the project as elements, and both at
    /// once shows each building twice.
    /// </remarks>
    LinkSiteIfc,

    /// <summary>
    /// Every building in the site model copied into the project as its own Generic Model element,
    /// the context terrain left out.
    /// </summary>
    /// <remarks>
    /// The site model's own extrusions, reused rather than rebuilt: it carries one per building, where
    /// the building mesh is one merged node and the footprints would have to be extruded here — see
    /// <c>docs/adr/0012-context-buildings-come-from-the-site-model.md</c>.
    /// </remarks>
    ContextBuildings,

    /// <summary>Publish the pre-derived survey point / shared coordinates.</summary>
    SetSharedCoordinates,

    /// <summary>
    /// Manage ▸ Location: the project's latitude and longitude, which is what places the sun.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SetSharedCoordinates"/> because the two read different fields and
    /// fail for different reasons: the survey point is a projected pair that may fall back to
    /// <c>delivery.local_origin</c>, and the site location is the own block's lat/lon and nothing
    /// else.
    /// </remarks>
    SetSiteLocation,

    /// <summary>
    /// Road centrelines from the <c>road_splines</c> vector layer — Forma's "Roads" row.
    /// </summary>
    RoadCentrelines,

    /// <summary>
    /// Property boundaries from the <c>land_use</c> vector layer — Forma's "Site limits" row.
    /// </summary>
    SiteBoundaries,

    /// <summary>
    /// Ground cover from the <c>land_cover</c> vector layer, cut as subdivisions the way the
    /// <see cref="SiteBoundaries"/> are. A different Overture layer from <c>land_use</c>, not a
    /// second name for it: this one carries the physical subtype — forest and its like.
    /// </summary>
    LandCover,

    /// <summary>
    /// The water bodies of the <c>water</c> vector layer, cut as subdivisions after the two land
    /// layers.
    /// </summary>
    /// <remarks>
    /// Polygons only. That layer's stream centrelines stay out: turning a centreline into an area
    /// means inventing a width, which is derivation and belongs upstream.
    /// </remarks>
    Water,

    /// <summary>
    /// Road surfaces from the <c>road_polygons</c> vector layer, cut as subdivisions.
    /// </summary>
    /// <remarks>
    /// The published surfaces, applied: already widened by the estimated width, merged per class and
    /// cut so no two overlap. They come in flat, at the terrain's own surface.
    /// </remarks>
    RoadPolygons,

    /// <summary>
    /// Trees from the tree-points file, with real height and crown — Forma's "Vegetation" row.
    /// </summary>
    Vegetation,

    /// <summary>
    /// The "Mantle Place Site Context" 3D view, and the view filter that finds every element an
    /// import stamped (<see cref="SiteContext"/>).
    /// </summary>
    SiteContextView,

    /// <summary>
    /// The satellite imagery draped on the terrain as a material texture — Forma's last row.
    /// </summary>
    ImageryDrape,

    /// <summary>
    /// The "Mantle Place Attribution" drafting view, and the provenance record on Project
    /// Information — which bundle this project came from, and whose data is in it.
    /// </summary>
    /// <remarks>
    /// Declared last so every kind before it keeps its number; its place in a plan is the planner's.
    /// </remarks>
    AttributionAndProvenance,
}

/// <summary>Which toposolid type the terrain step builds the ground on.</summary>
/// <remarks>
/// A role, not an element: the planner cannot see the document, so it decides WHICH type and the
/// shim resolves that to one. The name the imagery type goes by is
/// <see cref="DrapeLayering.ImageryName"/>, shared by both steps that touch it.
/// </remarks>
public enum TerrainToposolidType
{
    /// <summary>The project's own ground type, as <see cref="ToposolidTypeChoice"/> picks it.</summary>
    Project,

    /// <summary>
    /// A duplicate of that type whose top layer is the imagery drape's material — the type the drape
    /// step would otherwise retype the terrain onto after the fact.
    /// </summary>
    Imagery,
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

    /// <summary>
    /// The host's own <c>georeference.origin</c> publishes no latitude and longitude pair.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NoSurveyPoint"/>: that one is the projected pair, and a bundle can
    /// carry either without the other.
    /// </remarks>
    NoGeographicOrigin,

    /// <summary>
    /// The published latitude or longitude is off the globe, so it was not handed to Revit to throw
    /// on or to wrap.
    /// </summary>
    GeographicOriginOutOfRange,
    /// <summary>
    /// A layer derived from another is not in this bundle, while the layer it comes from is.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ArtifactNotInManifest"/>, and the distinction is what the curator is
    /// told: <c>road_polygons</c> is best-effort and its absence says nothing about whether the area
    /// has roads (<c>spec/format.md</c> §6.3), so "no road surfaces were derived for this order" is
    /// true where "no roads in this bundle" would not be. Re-downloading does not fix it.
    /// </remarks>
    DerivedLayerNotPublished,

    /// <summary>The bundle carries this layer and the curator left it out of the import window's checklist.</summary>
    /// <remarks>
    /// The one skip that is not about the bundle. A support triage rule reading a log has to tell "the
    /// trees did not come in" apart from "the trees were not wanted", and only this code does.
    /// </remarks>
    LeftOutByChoice,

    /// <summary>
    /// The bundle says this layer is absent, and no re-download can change that: a layer this host's
    /// own copy lacks had no features in the area, and imagery declared absent was unavailable for
    /// the site.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="ArtifactNotInManifest"/>, which is the bundle saying nothing — an absence
    /// the order can sometimes fix from the vault. The import window lists that one and stays silent
    /// on this one (<see cref="WindowLabels.UnavailableReason"/>), so the two must not share a code.
    /// Declared last so every code before it keeps its number.
    /// </remarks>
    DeclaredAbsent,
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
    /// (<c>hosts.revit.readiness.&lt;path&gt;.reason</c>) that reason is surfaced rather than replaced —
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

/// <summary>
/// The project's latitude and longitude, from <c>hosts.revit.georeference.origin</c> and nothing
/// else (<c>HPS-33</c>).
/// </summary>
/// <remarks>
/// <para>
/// Degrees as published, radians as Revit's <c>SiteLocation</c> takes them. The conversion is a unit
/// conversion and lives here with the check that the pair is on the globe, so the shim does no
/// arithmetic of its own. The sign goes through as published: Revit's own site database lists Boston
/// at -71.0335, west negative, which is the convention the manifest's lon/lat pair is in.
/// </para>
/// <para>
/// The time zone is <c>location.time_zone</c>'s and nobody else's. Revit derives one from longitude
/// whenever a latitude or longitude is set — documented on both setters — so the shim writes the
/// zone after the coordinates: the published one when there is one, and otherwise the project's own,
/// read before the coordinates moved it. A zone derived from longitude is derivation whoever does
/// it, and it is wrong wherever a political boundary is not a meridian.
/// </para>
/// </remarks>
public sealed class SiteLocationPlacement
{
    public required double LatitudeDeg { get; init; }

    public required double LongitudeDeg { get; init; }

    public double LatitudeRadians => LatitudeDeg * Math.PI / 180.0;

    public double LongitudeRadians => LongitudeDeg * Math.PI / 180.0;

    /// <summary>The published zone, or <c>null</c> when the bundle published none this host can read.</summary>
    public SiteTimeZone? TimeZone { get; init; }

    /// <summary>
    /// The hours to write to <c>SiteLocation.TimeZone</c>: the published zone's, or else
    /// <paramref name="projectHours"/>, the zone the project had before the coordinates changed.
    /// </summary>
    public double TimeZoneToWrite(double projectHours) => TimeZone?.RevitHours ?? projectHours;

    /// <summary>
    /// The import log's line for this step, given the zone the project had before it ran.
    /// </summary>
    /// <remarks>
    /// Here rather than in the shim because every branch of it is a decision this host made about a
    /// published value — kept, applied, wrapped, or refused — and a decision is asserted headlessly
    /// (<c>HPS-02</c>).
    /// </remarks>
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

/// <summary>
/// A published time zone fitted to Revit's <c>SiteLocation.TimeZone</c>, which takes −12 to +12
/// hours and nothing past them.
/// </summary>
/// <remarks>
/// <para>
/// Zones east of +12 exist — Tonga and Samoa at +13, Kiritimati at +14, the Chatham Islands at
/// +12:45 — and the spec publishes their offsets unclamped and leaves the fit to the host
/// (<c>spec/format.md</c> §4.1). This host wraps by 24 hours: +13 is written as −11. The clock time
/// is kept and the date moves one day later, which moves the sun by less than half a degree.
/// Clamping to +12 instead would keep the date and put every hour of a sun study one hour out, which
/// is the larger error by far. No zone lies west of −12; one that did would wrap the other way.
/// </para>
/// <para>
/// An offset of a day or more is not a time zone, so there is nothing to wrap and nothing is written.
/// </para>
/// </remarks>
public sealed class SiteTimeZone
{
    /// <summary>The largest offset, either side of UTC, that Revit accepts.</summary>
    public const double RevitLimitHours = 12.0;

    public required PublishedTimeZone Published { get; init; }

    /// <summary>The hours written to Revit, or <c>null</c> when the published offset is not a zone.</summary>
    public double? RevitHours => Wrap(Published.UtcOffsetStandardH);

    /// <summary>True when the offset had to be moved by a day to fit Revit's range.</summary>
    public bool IsWrapped => RevitHours is { } hours && hours != Published.UtcOffsetStandardH;

    /// <summary>
    /// The log's sentences about the zone: what was written and why, or why the project's own,
    /// <paramref name="projectHours"/>, was kept.
    /// </summary>
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
            true => " The zone observes daylight saving time, and an add-in cannot switch Revit's on: tick the "
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

    /// <summary><c>UTC-7</c>, <c>UTC+5:30</c>, <c>UTC+12:45</c>, <c>UTC+0</c>.</summary>
    public static string FormatUtc(double hours)
    {
        int minutes = (int)Math.Round(Math.Abs(hours) * 60.0, MidpointRounding.AwayFromZero);
        string sign = hours < 0.0 && minutes > 0 ? "-" : "+";
        return minutes % 60 == 0
            ? string.Format(CultureInfo.InvariantCulture, "UTC{0}{1}", sign, minutes / 60)
            : string.Format(CultureInfo.InvariantCulture, "UTC{0}{1}:{2:00}", sign, minutes / 60, minutes % 60);
    }
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
    /// How a vector layer's coordinates reach <see cref="Frame"/>: lon/lat, absolute in the origin's
    /// CRS, or offsets about it, each in its file's unit. Populated only for the vector layers.
    /// </summary>
    public LayerFrame? Layer { get; init; }

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

    /// <summary>Populated only for <see cref="ImportStepKind.SetSiteLocation"/>.</summary>
    public SiteLocationPlacement? SiteLocation { get; init; }

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

    /// <summary>
    /// The closed vocabulary this step's <c>foliage_type</c> values are drawn from, as
    /// <c>landcover.tree_points.foliage_type_vocabulary</c> published it. Read only for
    /// <see cref="ImportStepKind.Vegetation"/>.
    /// </summary>
    /// <remarks>
    /// Carried on the step for the same reason <see cref="Frame"/> and <see cref="Crop"/> are: the
    /// manifest is the authority on what the CSV's values mean, and the shim must not have to go
    /// looking for it. <c>null</c> is <em>the manifest named no vocabulary</em> — never "the one
    /// this build knows" — and it is what every bundle before MPB 1.2.0 carries
    /// (<see cref="FoliageTypes"/>).
    /// </remarks>
    public string? FoliageTypeVocabulary { get; init; }

    /// <summary>
    /// The area of interest, in local metres — points outside it are dropped before Revit sees
    /// them. Populated for <see cref="ImportStepKind.ToposurfaceFromPointsFile"/> and
    /// <see cref="ImportStepKind.ToposurfaceFromSurfaceTin"/>.
    /// </summary>
    /// <remarks>
    /// Carried on the step for the same reason <see cref="Frame"/> and <see cref="Drape"/> are: the
    /// decision — including the decision that there is no usable window — is the interesting part,
    /// and it belongs where a headless test reaches it. <c>null</c> is a stated degradation, never a
    /// refusal: the bbox-free fill guard still runs on either path (<see cref="SurfaceGrid"/> for
    /// the points file, <see cref="SurfaceTinSanitiser"/> for the TIN). Why the crop is needed at
    /// all is <see cref="SurfacePointsSanitiser"/>.
    /// </remarks>
    public SurfaceCropWindow? Crop { get; init; }

    /// <summary>
    /// The type the terrain is built on. Read only for
    /// <see cref="ImportStepKind.ToposurfaceFromPointsFile"/> and
    /// <see cref="ImportStepKind.ToposurfaceFromSurfaceTin"/>, the two kinds that build a toposolid.
    /// </summary>
    /// <remarks>
    /// ⛔ <see cref="TerrainToposolidType.Imagery"/> exactly when the plan also carries an
    /// <see cref="ImportStepKind.ImageryDrape"/> step. Retyping a toposolid makes Revit rebuild the
    /// whole terrain's element relations, and that cost tracks the point count: 409 s on an
    /// 80,372-point terrain, the largest single cost in the import, spent on a type the terrain step
    /// could have used at creation. Only a drape that will run earns the imagery type — one with no
    /// photograph to carry would lay a blank layer over the ground.
    /// </remarks>
    public TerrainToposolidType ToposolidType { get; init; } = TerrainToposolidType.Project;

    /// <summary>
    /// Populated only for <see cref="ImportStepKind.AttributionAndProvenance"/>: what the attribution view says
    /// and what the provenance record stores, both copied from the manifest.
    /// </summary>
    public ProjectProvenance? Provenance { get; init; }
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
