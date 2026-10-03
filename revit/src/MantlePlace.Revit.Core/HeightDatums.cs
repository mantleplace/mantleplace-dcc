namespace MantlePlace.Revit.Core;

/// <summary>
/// The vertical datum one file's heights are stated in, as its manifest stated it, and whether the
/// manifest owed a statement.
/// </summary>
/// <param name="Stated">The <c>vertical_datum</c> string verbatim, or <c>null</c> where none was stated.</param>
/// <param name="Owed">
/// Whether the manifest is MPB 1.8.0 or later, from which every height names its datum. The version
/// decides whether a datum is owed, never the presence of the key.
/// </param>
public readonly record struct StatedDatum(string? Stated, bool Owed);

/// <summary>The ground a step's heights land on, and the datum it was built in.</summary>
/// <param name="ElementId">The ground toposolid's <c>ElementId</c> value.</param>
/// <param name="Recorded">
/// The datum its stamp records, or <c>null</c> for a ground that records none: one built from a
/// manifest before MPB 1.8.0, or by a build of this plugin from before the record existed.
/// </param>
public readonly record struct GroundDatum(long ElementId, string? Recorded);

/// <summary>
/// Whether a step's heights may be placed on the ground this project already holds: a frame names its
/// vertical datum as well as its CRS and its unit (<c>HPS-53</c>).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ The failure this guards reports success. From MPB 1.8.0 a US order's content in the delivery CRS
/// is in <c>NAVD88 (GEOID18)</c>, where every earlier bundle was <c>EGM2008-orthometric</c>. A re-import
/// of the 1.8.0 rebuild into a project whose ground came from an earlier build keeps that ground (the
/// terrain's stale arm, <see cref="TerrainIdentity"/>), and every road, tree, contour and building
/// would land on it up to a metre off, with nothing in the log about heights. Within one bundle every
/// height Revit places shares one datum; this is what holds across two.
/// </para>
/// <para>
/// So the terrain step records the ground's datum in its stamp, and each step that places a height
/// compares its content's statement with that record. A difference is a named skip and the rest of
/// the import runs. ⛔ Nothing is converted and no offset is applied:
/// <c>elevation.dem.navd88_minus_egm2008_m</c> is one number for the whole site, and applying it would
/// derive a placement value, which the thin-client boundary refuses (<c>HPS-33</c>).
/// </para>
/// <para>
/// <b>How <em>unstated</em> compares.</b> Every bundle before 1.8.0 was EGM2008, so where nothing is
/// stated and nothing was owed, the version stands in for <see cref="Egm2008"/> — on either side. A
/// stated datum always outranks it, and content unstated on a manifest that owed a statement is a
/// producer's gap, refused rather than assumed to match. A datum this plugin does not know fails
/// closed, naming the value, even against the same string (<c>spec/compatibility.md</c> §3). The rule
/// is the one the Unreal host's road-spline reader applies, and both are pinned by corpus cases.
/// </para>
/// <para>
/// With no ground of this order in the project there is nothing to compare against, and a step places
/// as it always has. The ground is the reference, not the bundle: a first import builds its ground
/// in the bundle's own datum, and its content agrees with it.
/// </para>
/// </remarks>
public static class HeightDatums
{
    /// <summary>The datum of every bundle before MPB 1.8.0, and of every order outside the US since.</summary>
    public const string Egm2008 = "EGM2008-orthometric";

    /// <summary>The datum of a US order's content in the delivery CRS from MPB 1.8.0.</summary>
    public const string Navd88Geoid18 = "NAVD88 (GEOID18)";

    /// <summary>The first manifest version that names the datum of every height.</summary>
    public const string StatedFromVersion = "1.8.0";

    /// <summary>
    /// Whether a step of <paramref name="kind"/> places heights on the ground, and so is compared with
    /// it: the road centrelines, the trees' <c>ground_z</c>, the published contours, and the buildings,
    /// copied or linked.
    /// </summary>
    /// <remarks>
    /// The polygon layers are not here: a subdivision takes its height from the ground it is cut
    /// from. Nor are the hazard plan, which is drawn flat on a level, or the drape, which is a picture.
    /// </remarks>
    public static bool PlacesHeights(ImportStepKind kind)
        => kind is ImportStepKind.RoadCentrelines
            or ImportStepKind.Vegetation
            or ImportStepKind.PublishedContours
            or ImportStepKind.ContextBuildings
            or ImportStepKind.LinkSiteIfc;

    /// <summary>Whether a step of <paramref name="kind"/> builds the ground, and so records its datum.</summary>
    public static bool BuildsGround(ImportStepKind kind)
        => kind is ImportStepKind.ToposurfaceFromPointsFile or ImportStepKind.ToposurfaceFromSurfaceTin;

    /// <summary>
    /// What a step of <paramref name="kind"/> placing <paramref name="artifact"/> carries
    /// (<see cref="ImportStep.HeightDatum"/>), or <c>null</c> for a kind that neither places a height
    /// nor builds the ground.
    /// </summary>
    /// <remarks>
    /// A file this host's own block points at is in the block's datum unless it states its own: the
    /// block's <c>vertical_datum</c> covers every file it points at, and the own vector layers state
    /// none beside it. So does a deliverable the block names
    /// (<see cref="BundleArtifact.NamedByOwnBlock"/>), the site model's <c>ifc_site</c> among them,
    /// where <c>buildings.ifc.vertical_datum</c> is optional. That covering statement is read on the
    /// terms the ground's is (<see cref="GroundStatement"/>), so before 1.8.0 it is no statement at all. A file stating its
    /// own is read verbatim at any version. A host-neutral file has only its own.
    /// </remarks>
    public static StatedDatum? For(ImportStepKind kind, BundleArtifact? artifact, BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        string? block = GroundStatement(manifest.Georeference.VerticalDatum, manifest.Version);
        if (BuildsGround(kind))
        {
            return new StatedDatum(block, IsOwed(manifest.Version));
        }

        if (!PlacesHeights(kind) || artifact is null)
        {
            return null;
        }

        StatedDatum own = Statement(artifact.VerticalDatum, manifest.Version);
        return own.Stated is null && (artifact.FromOwnBlock || artifact.NamedByOwnBlock)
            ? own with { Stated = block }
            : own;
    }

    /// <summary>Whether a manifest of <paramref name="manifestVersion"/> owes a datum for every height.</summary>
    public static bool IsOwed(string? manifestVersion)
        => !ManifestVersion.IsBelowFloor(manifestVersion, StatedFromVersion);

    /// <summary>A file's statement, read verbatim; a blank string is unstated.</summary>
    public static StatedDatum Statement(string? stated, string? manifestVersion)
        => new(string.IsNullOrWhiteSpace(stated) ? null : stated, IsOwed(manifestVersion));

    /// <summary>
    /// The datum a ground built from this manifest records: this host's block's
    /// <c>vertical_datum</c> from MPB 1.8.0 on, and <c>null</c> before it.
    /// </summary>
    /// <remarks>
    /// Before 1.8.0 that member was a free string the format did not document, so it is not read as a
    /// statement. The ground records it as unstated, which compares as EGM2008 — what every bundle of
    /// that era was.
    /// </remarks>
    public static string? GroundStatement(string? blockDatum, string? manifestVersion)
        => Statement(blockDatum, manifestVersion) is { Owed: true, Stated: { } stated } ? stated : null;

    /// <summary>
    /// The ground a step's heights land on: this order's stamped ground, preferring the one this
    /// import is already using; <c>null</c> when the project holds no ground of this order.
    /// </summary>
    /// <param name="used">The ground this import built or kept, or any id no ground has where it has none yet.</param>
    public static GroundDatum? GroundFor(IReadOnlyList<ExistingTerrain> grounds, string cacheKeyStem, long used)
    {
        ArgumentNullException.ThrowIfNull(grounds);
        ArgumentNullException.ThrowIfNull(cacheKeyStem);

        ExistingTerrain? first = null;
        foreach (ExistingTerrain ground in grounds)
        {
            if (!TerrainIdentity.IsStampFor(ground.Comments, cacheKeyStem))
            {
                continue;
            }

            if (ground.ElementId == used)
            {
                return new GroundDatum(ground.ElementId, TerrainIdentity.DatumOf(ground.Comments, cacheKeyStem));
            }

            first ??= ground;
        }

        return first is { } mine
            ? new GroundDatum(mine.ElementId, TerrainIdentity.DatumOf(mine.Comments, cacheKeyStem))
            : null;
    }

    /// <summary>
    /// Why <paramref name="content"/>'s heights may not be placed on <paramref name="ground"/>, in one
    /// sentence for the import's log, or <c>null</c> where they may.
    /// </summary>
    /// <param name="label">The content, as the import names it to the curator: "trees", "road centrelines".</param>
    public static string? Refusal(GroundDatum? ground, StatedDatum content, string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        if (ground is not { } on)
        {
            return null;
        }

        string remedy = $" To bring them in, delete toposolid {on.ElementId} and import again.";

        string contentDatum;
        if (content.Stated is { } stated)
        {
            if (!IsKnown(stated))
            {
                return $"The {label} state their heights in \"{stated}\", a vertical datum this plugin does not "
                    + "know, so they were not placed: it converts no height and cannot show they sit on the ground.";
            }

            contentDatum = stated;
        }
        else if (content.Owed)
        {
            return $"The file the {label} come from states no vertical datum for its heights, as a bundle from "
                + $"MPB {StatedFromVersion} on does for every height, and an unstated datum is never taken to "
                + "match the ground, so they were not placed.";
        }
        else
        {
            contentDatum = Egm2008;
        }

        string groundDatum;
        string groundSays;
        if (on.Recorded is { } recorded)
        {
            if (!IsKnown(recorded))
            {
                return $"The terrain the {label} would stand on, toposolid {on.ElementId}, records its heights "
                    + $"in \"{recorded}\", a vertical datum this plugin does not know, so they were not placed."
                    + remedy;
            }

            groundDatum = recorded;
            groundSays = $"records its heights in \"{recorded}\"";
        }
        else
        {
            groundDatum = Egm2008;
            groundSays = $"records no vertical datum, which reads as \"{Egm2008}\", the datum of every bundle "
                + $"before MPB {StatedFromVersion}";
        }

        if (string.Equals(contentDatum, groundDatum, StringComparison.Ordinal))
        {
            return null;
        }

        return $"The {label} state their heights in \"{contentDatum}\", but the terrain they would stand on, "
            + $"toposolid {on.ElementId}, {groundSays}. Placed as they are they would not sit on the ground, "
            + "and this plugin converts no height, so they were not placed and nothing was shifted." + remedy;
    }

    /// <summary>What a height step's content is called in its refusal: "trees", "road centrelines".</summary>
    public static string Noun(ImportStepKind kind) => kind switch
    {
        ImportStepKind.RoadCentrelines => "road centrelines",
        ImportStepKind.Vegetation => "trees",
        ImportStepKind.PublishedContours => "published contours",
        ImportStepKind.ContextBuildings => "context buildings",
        ImportStepKind.LinkSiteIfc => "linked site model's buildings",
        _ => kind.ToString(),
    };

    /// <summary>Whether this plugin knows <paramref name="datum"/>, compared verbatim.</summary>
    public static bool IsKnown(string datum)
        => string.Equals(datum, Egm2008, StringComparison.Ordinal)
            || string.Equals(datum, Navd88Geoid18, StringComparison.Ordinal);
}
