using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The site-boundary step: subdivisions cut into the ground, stamped so a re-import finds them.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Property boundaries as toposolid subdivisions — Forma's "Site limits" row, by the mechanism
    /// Forma itself offers alongside Model Lines.
    /// </summary>
    /// <remarks>
    /// A subdivision rather than a model line because the land-use polygons are 2-D: the GeoJSON
    /// carries no third ordinate, so there is no honest elevation to draw them at. A subdivision is
    /// projected onto the toposolid and follows the relief, which is both what Forma produces and
    /// the only reading that does not need an elevation nobody published. With no toposolid in the
    /// document there is nothing to project onto, and that is said rather than worked around.
    /// </remarks>
    private void ImportSiteBoundaries(ImportStep step)
    {
        if (ReadVectorLayer(step, SiteGeometryKinds.Areas, "site boundaries") is not { } features)
        {
            return;
        }

        ElementId terrainId = _terrainId != ElementId.InvalidElementId ? _terrainId : TerrainToposolidId();
        if (_document.GetElement(terrainId) is not Toposolid terrain)
        {
            Say(
                $"Skipped the site boundaries ({step.EntryName}): they are draped onto the terrain as toposolid "
                + "subdivisions, and this project has no toposolid. Import the terrain first, then re-run.");
            return;
        }

        // Which features are already on the terrain is decided in the pure core from the stamps the
        // existing subdivisions carry, so a re-import creates nothing twice.
        IReadOnlyList<NewSiteBoundary> newBoundaries = SiteBoundaryIdentity.NewFeatures(
            ExistingBoundaryStamps(terrain),
            [.. features.Select(feature => (string?)feature.Name)],
            _archive.Layout.Key.Stem);
        int alreadyPresent = features.Count - newBoundaries.Count;

        int created = 0;
        int declined = 0;
        int unstamped = 0;

        // Which features end up with a subdivision on the terrain: everything already present, plus
        // whatever this run manages to cut. A ring Revit declines, or one with too few edges to
        // close, leaves nothing behind — and a report naming a subdivision that does not exist is
        // the same lie as a summary counting one.
        bool[] onTerrain = new bool[features.Count];
        Array.Fill(onTerrain, true);
        foreach (NewSiteBoundary boundary in newBoundaries)
        {
            onTerrain[boundary.Ordinal - 1] = false;
        }

        // ⛔ Before the transaction, and before this run cuts anything: whether a parent toposolid's
        // bounding box absorbs its subdivisions is unexecuted Revit behaviour, so the ground is
        // measured while the only subdivisions on it are ones an earlier import left. A re-import
        // still measures it with those present — if Revit does absorb them, and one reaches past the
        // ground it was cut from, this footprint is that much too large and the comparison below
        // under-reports. It never over-reports, which is the direction that matters for a line
        // asserting a subdivision is redundant.
        FootprintExtent? ground = GroundFootprint(terrain);

        // ⛔ Before the transaction, because the whole cost is inside its commit and nothing can be
        // written while that runs. This line is the only warning there will ever be.
        if (SlowStepNotice.For(step.Kind, _terrainVertexCount, newBoundaries.Count) is { } notice)
        {
            Say(notice);
        }

        ImportFailureSwallower swallower = new("Importing the site boundaries");
        using Transaction transaction = BeginTransaction("Mantle Place: site boundaries", swallower);

        foreach (NewSiteBoundary boundary in newBoundaries)
        {
            SiteFeature feature = features[boundary.Ordinal - 1];
            List<Curve> edges = [];
            for (int index = 0; index < feature.Vertices.Count; index++)
            {
                SiteVertex from = feature.Vertices[index];
                SiteVertex to = feature.Vertices[(index + 1) % feature.Vertices.Count];

                // Flat by construction: a subdivision profile is projected onto the toposolid, so the
                // loop's own elevation is irrelevant and zero keeps it well inside Revit's tolerance.
                XYZ start = new(MetresToInternal(from.EastM), MetresToInternal(from.NorthM), 0.0);
                XYZ end = new(MetresToInternal(to.EastM), MetresToInternal(to.NorthM), 0.0);
                if (start.DistanceTo(end) > _document.Application.ShortCurveTolerance)
                {
                    edges.Add(Line.CreateBound(start, end));
                }
            }

            if (edges.Count < 3)
            {
                continue;
            }

            try
            {
                Toposolid subdivision = terrain.CreateSubDivision(_document, [CurveLoop.Create(edges)]);
                created++;
                onTerrain[boundary.Ordinal - 1] = true;

                // Remembered for the drape, which prefers the stamp below but cannot use it for a
                // subdivision that fails to take one.
                _createdSubDivisionIds.Add(subdivision.Id);

                // The plugin's first parameter write. Comments is the subdivision's identity for the
                // NEXT import — a subdivision it could not stamp is kept (the boundary is real), it
                // just cannot be recognised later, and that is said in the log rather than hidden.
                Parameter? comments = subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comments is null || comments.IsReadOnly || !comments.Set(boundary.Stamp))
                {
                    unstamped++;
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                // A ring that self-intersects, or falls outside the terrain, is one boundary lost —
                // not a reason to abandon the other two.
                declined++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        string summary = $"Imported {created:N0} site boundary subdivision(s) from {step.EntryName}";
        if (alreadyPresent > 0)
        {
            summary += $"; {alreadyPresent:N0} from an earlier import of this bundle were already present and left alone";
        }

        if (declined > 0)
        {
            summary += $"; Revit declined {declined:N0} that did not lie cleanly on the terrain";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        Say(summary + ".");
        ReportCoextensiveBoundaries(ground, features, onTerrain);
    }

    /// <summary>
    /// The ground toposolid's footprint in the bundle's own east/north metres, or <c>null</c> when
    /// Revit will not give a bounding box.
    /// </summary>
    /// <remarks>
    /// The same box <c>TerrainProbeCommand</c>'s inventory prints. It is comparable with a ring's
    /// vertices because those are placed at <see cref="MetresToInternal"/> of frame metres with no
    /// further translation — <em>for a terrain this plugin built</em>. A terrain found by
    /// <see cref="TerrainToposolidId"/> may be a curator's own or another bundle's, in which case
    /// the two are in the same model axes but not necessarily about the same origin, and a
    /// comparison between them is meaningless rather than wrong.
    /// </remarks>
    private FootprintExtent? GroundFootprint(Toposolid terrain)
    {
        if (terrain.get_BoundingBox(null) is not { } box)
        {
            Trace("  site boundaries: the terrain has no bounding box, so no footprint could be compared.");
            return null;
        }

        return new FootprintExtent(
            InternalToMetres(box.Min.X),
            InternalToMetres(box.Min.Y),
            InternalToMetres(box.Max.X),
            InternalToMetres(box.Max.Y));
    }

    /// <summary>
    /// Says which published boundaries reproduce the ground's own outline, and nothing else about
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every feature that ENDED UP on the terrain is measured, not just the ones this run created:
    /// a re-import creates nothing and the reading is just as true the second time. A feature with
    /// no subdivision behind it — declined by Revit, or too few edges to close — is passed over,
    /// because a line naming a subdivision that is not in the model is the same lie as a count of
    /// one that was never cut. The verdict is <see cref="SiteBoundaryCoextension"/>'s
    /// (<c>HPS-02</c>); this reads the two footprints and says whatever comes back.
    /// </para>
    /// <para>
    /// The subdivision's footprint is the box around the ring this import drew, in the bundle's own
    /// east/north metres; the ground's comes from <see cref="GroundFootprint"/>, which says how the
    /// two are made comparable.
    /// </para>
    /// <para>
    /// ⛔ Nothing is skipped, refused or arbitrated on the strength of this. The published polygons
    /// are what the bundle publishes and this host applies what it is given.
    /// </para>
    /// </remarks>
    private void ReportCoextensiveBoundaries(
        FootprintExtent? ground,
        IReadOnlyList<SiteFeature> features,
        bool[] onTerrain)
    {
        if (ground is not { } groundFootprint)
        {
            return;
        }

        for (int index = 0; index < features.Count; index++)
        {
            if (!onTerrain[index] || FootprintExtent.Around(features[index].Vertices) is not { } footprint)
            {
                continue;
            }

            // The position is stated by the core alongside whatever name there is, because names are
            // not unique and an unnamed feature's stamp is its position (SiteBoundaryIdentity).
            string name = FeatureName(features[index], "(unnamed)");

            if (SiteBoundaryCoextension.Describe(name, index + 1, footprint, groundFootprint) is { } line)
            {
                Say(line);
            }
        }
    }

    /// <summary>
    /// The stamps this plugin wrote onto the terrain's existing subdivisions — anything else in the
    /// Comments parameter, including nothing at all, is a curator's and is not an identity here.
    /// </summary>
    private HashSet<string> ExistingBoundaryStamps(Toposolid terrain)
    {
        HashSet<string> stamps = new(StringComparer.Ordinal);
        foreach (ElementId id in terrain.GetSubDivisionIds())
        {
            if (_document.GetElement(id) is Toposolid subdivision
                && subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is { } comments
                && comments.AsString() is { Length: > 0 } stamp)
            {
                stamps.Add(stamp);
            }
        }

        return stamps;
    }
}
