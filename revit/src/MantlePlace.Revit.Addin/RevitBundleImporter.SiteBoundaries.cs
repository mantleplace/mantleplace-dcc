using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The subdivision steps: the published polygon layers — land use, land cover, water bodies and road
// surfaces — cut into the ground, stamped so a re-import finds them.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// A published polygon layer as toposolid subdivisions. For <c>land_use</c> that is property
    /// boundaries — Forma's "Site limits" row, by the mechanism Forma itself offers alongside Model
    /// Lines — and for <c>land_cover</c>, <c>water</c> and <c>road_polygons</c> the ground cover, the
    /// water bodies and the road surfaces, cut the same way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A subdivision rather than a model line because the polygons are 2-D: the GeoJSON carries no
    /// third ordinate, so there is no honest elevation to draw them at. A subdivision is projected
    /// onto the toposolid and follows the relief, which is both what Forma produces and the only
    /// reading that does not need an elevation nobody published. With no toposolid in the document
    /// there is nothing to project onto, and that is said rather than worked around. A road surface
    /// therefore comes in flat, at the terrain's own surface, rather than recessed into it.
    /// </para>
    /// <para>
    /// What each layer's rings become is <see cref="GroundCuts"/>'s: a land ring is its own
    /// subdivision, while a water body or a road network is cut whole, with its islands and city
    /// blocks left out of it. Nothing is clipped against another layer and no layer takes precedence
    /// — the published polygons overlap because the ground they describe does, and Revit keeps them
    /// all (<see cref="ImportFailurePolicy"/>).
    /// </para>
    /// <para>
    /// The renderer keyword each cut's material will carry is decided there too, and remembered by
    /// element for the drape (<see cref="_subDivisionKeywords"/>) — for a subdivision this run cut,
    /// and for one an earlier import left, alike.
    /// </para>
    /// </remarks>
    private void ImportSiteBoundaries(ImportStep step, GroundLayer layer)
    {
        GroundLayerWords words = GroundLayerWords.For(layer);
        string label = words.Label;

        if (ReadVectorLayer(step, SiteGeometryKinds.Areas, label) is not { } rings)
        {
            return;
        }

        (IReadOnlyList<GroundCut> cuts, int strandedHoles) = GroundCuts.For(layer, rings);
        if (cuts.Count == 0)
        {
            Say($"The {label} layer ({step.EntryName}) carries no polygon this plugin could cut: "
                + $"{strandedHoles:N0} ring(s) are holes whose own polygon could not be read.");
            return;
        }

        ElementId terrainId = _terrainId != ElementId.InvalidElementId ? _terrainId : TerrainToposolidId();
        if (_document.GetElement(terrainId) is not Toposolid terrain)
        {
            Say(
                $"Skipped the {label} ({step.EntryName}): they are draped onto the terrain as toposolid "
                + "subdivisions, and this project has no toposolid. Import the terrain first, then re-run.");
            return;
        }

        // Which cuts are already on the terrain is decided in the pure core from the stamps the
        // existing subdivisions carry, so a re-import creates nothing twice.
        IReadOnlyList<string?> names = GroundCuts.Names(cuts);
        string stem = _archive.Layout.Key.Stem;
        IReadOnlyList<NewSiteBoundary> newBoundaries = SiteBoundaryIdentity.NewFeatures(
            layer,
            ExistingBoundaryStamps(terrain),
            names,
            stem);
        int alreadyPresent = cuts.Count - newBoundaries.Count;

        // The keywords for the subdivisions an earlier import cut, found by the stamp each carries.
        // The ones this run cuts are remembered below, as each is created.
        RememberKeywords(terrain, layer, GroundCuts.KeywordsByStamp(cuts, SiteBoundaryIdentity.Stamps(layer, names, stem)));

        int created = 0;
        int declined = 0;
        int unstamped = 0;

        // A hole that is not cut is ground the subdivision covers and the bundle says it does not,
        // so both ways of losing one are counted and said.
        int holesUncut = 0;

        // Which cuts end up with a subdivision on the terrain: everything already present, plus
        // whatever this run manages to cut. A ring Revit declines, or one with too few edges to
        // close, leaves nothing behind — and a report naming a subdivision that does not exist is
        // the same lie as a summary counting one.
        bool[] onTerrain = new bool[cuts.Count];
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

        ImportFailureSwallower swallower = new($"Importing the {label}");
        using Transaction transaction = BeginTransaction($"Mantle Place: {label}", swallower);

        foreach (NewSiteBoundary boundary in newBoundaries)
        {
            GroundCut cut = cuts[boundary.Ordinal - 1];
            if (Loop(cut.Outer) is not { } outer)
            {
                continue;
            }

            // The outer loop first and the holes after it, which is the order Revit reads them in:
            // an outer loop with its inner loops comes back as one subdivision with the holes left
            // out of it, in Revit 2025, 2026 and 2027 alike, whichever way round a ring is wound.
            List<CurveLoop> loops = [outer];
            int uncut = 0;
            foreach (SiteFeature hole in cut.Holes)
            {
                if (Loop(hole) is { } inner)
                {
                    loops.Add(inner);
                }
                else
                {
                    uncut++;
                }
            }

            try
            {
                Toposolid subdivision = terrain.CreateSubDivision(_document, loops);
                created++;

                // Counted only now: there is no subdivision for a hole to be missing from until
                // this call returns.
                holesUncut += uncut;
                onTerrain[boundary.Ordinal - 1] = true;

                // Remembered for the drape, which prefers the stamp below but cannot use it for a
                // subdivision that fails to take one.
                _createdSubDivisionIds.Add(subdivision.Id);
                if (cut.Keyword is { } keyword)
                {
                    _subDivisionKeywords[subdivision.Id] = keyword;
                }

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
                // not a reason to abandon the other two. Its holes go with it and are not counted
                // above: there is no subdivision left for them to be holes in.
                declined++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        string summary = $"Imported {created:N0} {words.Noun} subdivision(s) from {step.EntryName}";
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

        if (holesUncut > 0)
        {
            summary += $"; {holesUncut:N0} hole(s) had too few edges to cut, so that much ground is covered";
        }

        if (strandedHoles > 0)
        {
            summary += $"; {strandedHoles:N0} hole(s) are holes of a polygon whose outer ring could not be "
                + "read, so they were left out with it";
        }

        Say(summary + ".");
        ReportCoextensiveBoundaries(ground, cuts, onTerrain);
    }

    /// <summary>
    /// One published ring as a closed Revit loop, or <c>null</c> when too few of its edges survive
    /// the short-curve tolerance to close one.
    /// </summary>
    /// <remarks>
    /// Flat by construction: a subdivision profile is projected onto the toposolid, so the loop's own
    /// elevation is irrelevant and zero keeps it well inside Revit's tolerance. A filled region's loop
    /// is drawn at its plan's level instead (<paramref name="z"/>), in the view's own plane.
    /// </remarks>
    private CurveLoop? Loop(SiteFeature ring, double z = 0.0)
    {
        List<Curve> edges = [];
        for (int index = 0; index < ring.Vertices.Count; index++)
        {
            SiteVertex from = ring.Vertices[index];
            SiteVertex to = ring.Vertices[(index + 1) % ring.Vertices.Count];

            XYZ start = new(MetresToInternal(from.EastM), MetresToInternal(from.NorthM), z);
            XYZ end = new(MetresToInternal(to.EastM), MetresToInternal(to.NorthM), z);
            if (start.DistanceTo(end) > _document.Application.ShortCurveTolerance)
            {
                edges.Add(Line.CreateBound(start, end));
            }
        }

        return edges.Count < 3 ? null : CurveLoop.Create(edges);
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
        IReadOnlyList<GroundCut> cuts,
        bool[] onTerrain)
    {
        if (ground is not { } groundFootprint)
        {
            return;
        }

        for (int index = 0; index < cuts.Count; index++)
        {
            if (!onTerrain[index] || FootprintExtent.Around(cuts[index].Outer.Vertices) is not { } footprint)
            {
                continue;
            }

            // The position is stated by the core alongside whatever name there is, because names are
            // not unique and an unnamed feature's stamp is its position (SiteBoundaryIdentity).
            string name = FeatureName(cuts[index].Outer, "(unnamed)");

            if (SiteBoundaryCoextension.Describe(name, index + 1, footprint, groundFootprint) is { } line)
            {
                Say(line);
            }
        }
    }

    /// <summary>
    /// Pairs each subdivision already on the terrain with the renderer keyword its published feature
    /// names, by the stamp the subdivision carries.
    /// </summary>
    /// <remarks>
    /// The pairing is <see cref="RendererKeywords.ByStamp"/>'s; this only reads the stamps off the
    /// elements. A stamp is matched in full, so a subdivision of the other layer, of another order,
    /// or a curator's own is never given a keyword here.
    /// </remarks>
    private void RememberKeywords(Toposolid terrain, GroundLayer layer, IReadOnlyDictionary<string, string> byStamp)
    {
        if (byStamp.Count == 0)
        {
            return;
        }

        foreach (ElementId id in terrain.GetSubDivisionIds())
        {
            if (_document.GetElement(id) is Toposolid subdivision
                && subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() is { } stamp
                && SiteBoundaryIdentity.Parse(stamp, _archive.Layout.Key.Stem)?.Layer == layer
                && byStamp.TryGetValue(stamp, out string? keyword))
            {
                _subDivisionKeywords[id] = keyword;
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
