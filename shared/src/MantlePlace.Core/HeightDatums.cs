namespace MantlePlace.Core;

public readonly record struct StatedDatum(string? Stated, bool Owed);

public readonly record struct GroundDatum(long ElementId, string? Recorded);

public static class HeightDatums
{
    public const string Egm2008 = "EGM2008-orthometric";

    public const string Navd88Geoid18 = "NAVD88 (GEOID18)";

    public const string StatedFromVersion = "1.8.0";

    public static bool PlacesHeights(ImportStepKind kind)
        => kind is ImportStepKind.RoadCentrelines
            or ImportStepKind.Vegetation
            or ImportStepKind.PublishedContours
            or ImportStepKind.ContextBuildings
            or ImportStepKind.LinkSiteIfc;

    public static bool BuildsGround(ImportStepKind kind)
        => kind is ImportStepKind.ToposurfaceFromPointsFile or ImportStepKind.ToposurfaceFromSurfaceTin;

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

    public static bool IsOwed(string? manifestVersion)
        => !ManifestVersion.IsBelowFloor(manifestVersion, StatedFromVersion);

    public static StatedDatum Statement(string? stated, string? manifestVersion)
        => new(string.IsNullOrWhiteSpace(stated) ? null : stated, IsOwed(manifestVersion));

    public static string? GroundStatement(string? blockDatum, string? manifestVersion)
        => Statement(blockDatum, manifestVersion) is { Owed: true, Stated: { } stated } ? stated : null;

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

    public static string Noun(ImportStepKind kind) => kind switch
    {
        ImportStepKind.RoadCentrelines => "road centrelines",
        ImportStepKind.Vegetation => "trees",
        ImportStepKind.PublishedContours => "published contours",
        ImportStepKind.ContextBuildings => "context buildings",
        ImportStepKind.LinkSiteIfc => "linked site model's buildings",
        _ => kind.ToString(),
    };

    public static string? FromFrame(FrameCrs crs, string? geoidModel)
    {
        ArgumentNullException.ThrowIfNull(crs);

        string? code = crs.Code?.Trim();
        if (string.Equals(code, Navd88HeightCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(geoidModel?.Trim(), Geoid18, StringComparison.OrdinalIgnoreCase))
        {
            return Navd88Geoid18;
        }

        if (string.Equals(code, Egm2008HeightCode, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(geoidModel) || string.Equals(geoidModel.Trim(), Egm2008Geoid, StringComparison.OrdinalIgnoreCase)))
        {
            return Egm2008;
        }

        return crs.Code ?? crs.Wkt2 ?? crs.ProjJson;
    }

    private const string Navd88HeightCode = "EPSG:5703";

    private const string Geoid18 = "GEOID18";

    private const string Egm2008HeightCode = "EPSG:3855";

    private const string Egm2008Geoid = "EGM2008";

    public static bool IsKnown(string datum)
        => string.Equals(datum, Egm2008, StringComparison.Ordinal)
            || string.Equals(datum, Navd88Geoid18, StringComparison.Ordinal);
}
