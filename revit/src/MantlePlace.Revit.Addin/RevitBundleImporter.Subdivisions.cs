using System.Diagnostics;
using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private void CutSubdivisions(ImportStep step, GroundLayer layer)
    {
        GroundLayerWords words = GroundLayerWords.For(layer);
        string label = words.Label;

        if (ReadVectorLayer(step, SiteVectorLayers.For(step.Kind).DrawnFrom, label) is not { } rings)
        {
            return;
        }

        (IReadOnlyList<GroundCut> cuts, int strandedHoles) = GroundCuts.For(rings);
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

        IReadOnlyList<string?> names = GroundCuts.Names(cuts);
        string stem = _archive.Layout.Key.Stem;
        IReadOnlyList<NewSiteBoundary> missing = SiteBoundaryIdentity.NewFeatures(
            layer,
            ExistingBoundaryStamps(terrain),
            names,
            stem);

        LevelCutSubdivisions leveled = FidelityCuts.Subdivisions(
            [.. cuts.Select(cut => cut.Outer.LowestLevel)],
            missing,
            step.Level);
        IReadOnlyList<NewSiteBoundary> newBoundaries = leveled.ToCut;
        int alreadyPresent = leveled.AlreadyPresent;

        GroundCutMember[] members = [.. newBoundaries.Select(boundary => new GroundCutMember(boundary, cuts[boundary.Ordinal - 1]))];
        IReadOnlyList<IReadOnlyList<int>> batches = GroundCutBatches.Of([.. members.Select(member => member.Cut)]);

        CutTally tally = new();
        Dictionary<ElementId, CutPiece> pieces = [];
        HashSet<ElementId> unstampedIds = [];

        int typedForDrape = 0;
        TimeSpan typingTime = TimeSpan.Zero;

        if (SlowStepNotice.For(step.Kind, _terrainVertexCount, batches.Count) is { } notice)
        {
            Announce(notice);
        }

        bool smoothed = TerrainSmoothing.SettleBeforeCuts(step.DrapePlanned, batches.Count)
            && EnsureSmoothedSurface();

        bool typingAnnounced = false;

        ImportFailureSwallower swallower = new($"Importing the {label}");
        using Transaction transaction = BeginTransaction($"Mantle Place: {label}", swallower);

        int handled = 0;
        foreach (IReadOnlyList<int> batch in batches)
        {
            Count(handled, members.Length);
            handled += batch.Count;

            int firstMade = tally.Made.Count;
            CutBatch(terrain, [.. batch.Select(index => members[index])], tally);

            for (int index = firstMade; index < tally.Made.Count; index++)
            {
                CutPiece piece = tally.Made[index];
                Toposolid made = piece.SubDivision;
                pieces[made.Id] = piece;
                _createdSubDivisionIds.Add(made.Id);

                string stamp = SiteBoundaryIdentity.Batch([.. piece.Members.Select(member => member.Boundary.Stamp)]);
                Parameter? comments = made.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comments is null || comments.IsReadOnly || !comments.Set(stamp))
                {
                    unstampedIds.Add(made.Id);
                }

                _stampsCutWith[made.Id] = stamp;

                long before = Stopwatch.GetTimestamp();
                SubDivisionMaterialRoute route = RouteAtCut(made, out ToposolidType? typeAsCut);
                if (!typingAnnounced
                    && SlowStepNotice.ForTypesAtCut(layer, route, step.DrapePlanned, batches.Count, _terrainVertexCount) is { } typing)
                {
                    typingAnnounced = true;
                    Announce(typing);
                }

                if (SubDivisionMaterial.TakesTypeAtCut(route, step.DrapePlanned)
                    && typeAsCut is not null
                    && TypeForDrapeAtCut(made, typeAsCut, smoothed))
                {
                    typedForDrape++;
                }

                typingTime += Stopwatch.GetElapsedTime(before);
            }
        }

        Count(members.Length, members.Length);

        swallower.OwnNewElements.UnionWith(pieces.Keys);

        Trace($"  cut: {members.Length:N0} {words.Noun} feature(s) in {batches.Count:N0} batch(es), "
            + $"{pieces.Count:N0} subdivision(s) made; {tally.BatchesSplit:N0} batch(es) Revit refused whole were cut one by one.");

        if (typedForDrape > 0)
        {
            Trace($"  cut: {typedForDrape:N0} of {pieces.Count:N0} subdivision(s) given their drape type "
                + $"as they were cut, in {typingTime.TotalSeconds:N1} s.");
        }

        if (!CommitAndReport(transaction, swallower))
        {
            ReportRefusedCuts(swallower, pieces);
            return;
        }

        int declined = tally.Declined;
        foreach (PostedError refusal in swallower.Deleted)
        {
            foreach (ElementId id in refusal.FailingElementIds)
            {
                if (!pieces.Remove(id, out CutPiece piece))
                {
                    continue;
                }

                declined += piece.Members.Count;
                _createdSubDivisionIds.Remove(id);
                _stampsCutWith.Remove(id);
                foreach (GroundCutMember member in piece.Members)
                {
                    Say(ImportFailurePolicy.ExplainRefusedSubDivision(
                        member.Boundary.Ordinal,
                        FeatureName(member.Cut.Outer, "(unnamed)"),
                        refusal.Text,
                        refusal.Id,
                        id.ToString()));
                }
            }
        }

        int created = pieces.Values.Sum(piece => piece.Members.Count);
        int unstamped = pieces.Where(pair => unstampedIds.Contains(pair.Key)).Sum(pair => pair.Value.Members.Count);
        int holesUncut = pieces.Values.Sum(piece => piece.HolesUncut);

        string summary = $"Imported {created:N0} {words.Noun} feature(s) from {step.EntryName}, cut as {pieces.Count:N0} subdivision(s)";
        if (alreadyPresent > 0)
        {
            summary += $"; {alreadyPresent:N0} from an earlier import of this bundle were already present and left alone";
        }

        summary += FidelityCuts.LevelClause(step.Level, leveled.LeftOut, leveled.KeptAboveLevel, "subdivisions");

        if (declined > 0)
        {
            summary += $"; Revit declined {declined:N0} that did not lie cleanly on the terrain";
        }

        if (tally.Unclosed > 0)
        {
            summary += $"; {tally.Unclosed:N0} could not be closed into a loop and were left out";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        if (holesUncut > 0)
        {
            summary += $"; {holesUncut:N0} hole(s) could not be closed into a loop, so that much ground is covered";
        }

        if (strandedHoles > 0)
        {
            summary += $"; {strandedHoles:N0} hole(s) are holes of a polygon whose outer ring could not be "
                + "read, so they were left out with it";
        }

        Say(summary + ".");
    }

    private void CutBatch(Toposolid terrain, IReadOnlyList<GroundCutMember> batch, CutTally tally)
    {
        List<CurveLoop> loops = [];
        List<GroundCutMember> closed = [];
        int holesUncut = 0;
        foreach (GroundCutMember member in batch)
        {
            if (Loop(member.Cut.Outer) is not { } outer)
            {
                tally.Unclosed++;
                continue;
            }

            loops.Add(outer);
            closed.Add(member);
            foreach (SiteFeature hole in member.Cut.Holes)
            {
                if (Loop(hole) is { } inner)
                {
                    loops.Add(inner);
                }
                else
                {
                    holesUncut++;
                }
            }
        }

        if (closed.Count == 0)
        {
            return;
        }

        try
        {
            tally.Made.Add(new CutPiece(terrain.CreateSubDivision(_document, loops), closed, holesUncut));
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            if (closed.Count == 1)
            {
                tally.Declined++;
                return;
            }

            tally.BatchesSplit++;
            foreach (GroundCutMember member in closed)
            {
                CutBatch(terrain, [member], tally);
            }
        }
    }

    private void ReportRefusedCuts(ImportFailureSwallower swallower, Dictionary<ElementId, CutPiece> pieces)
    {
        foreach (PostedError error in swallower.Errors)
        {
            foreach (ElementId id in error.FailingElementIds)
            {
                if (!pieces.TryGetValue(id, out CutPiece piece))
                {
                    Say(ImportFailurePolicy.ExplainRefusalNamedOther(error.Id, id.ToString()));
                    continue;
                }

                foreach (GroundCutMember member in piece.Members)
                {
                    Say(ImportFailurePolicy.ExplainRefusalNamedFeature(
                        error.Id, member.Boundary.Ordinal, FeatureName(member.Cut.Outer, "(unnamed)"), id.ToString()));
                }
            }

            if (error.FailingElementIds.Count == 0)
            {
                Say(ImportFailurePolicy.ExplainRefusalNamedNothing(error.Id));
            }
        }
    }

    private CurveLoop? Loop(SiteFeature ring, double z = 0.0)
    {
        if (SiteRings.Thin(ring.Vertices, ShortCurveToleranceM) is not { } vertices)
        {
            return null;
        }

        try
        {
            List<Curve> edges = [];
            for (int index = 0; index < vertices.Count; index++)
            {
                SiteVertex from = vertices[index];
                SiteVertex to = vertices[(index + 1) % vertices.Count];
                edges.Add(Line.CreateBound(
                    SitePoint(from.EastM, from.NorthM, z),
                    SitePoint(to.EastM, to.NorthM, z)));
            }

            return CurveLoop.Create(edges);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
        {
            Trace($"  a ring of {ring.Vertices.Count:N0} vertices could not be closed: {ex.Message}");
            return null;
        }
    }

    private HashSet<string> ExistingBoundaryStamps(Toposolid terrain)
    {
        HashSet<string> stamps = new(StringComparer.Ordinal);
        foreach (ElementId id in terrain.GetSubDivisionIds())
        {
            if (_document.GetElement(id) is Toposolid subdivision
                && subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is { } comments
                && comments.AsString() is { Length: > 0 } stamp)
            {
                stamps.UnionWith(SiteBoundaryIdentity.Members(stamp));
            }
        }

        return stamps;
    }

    private readonly record struct GroundCutMember(NewSiteBoundary Boundary, GroundCut Cut);

    private readonly record struct CutPiece(Toposolid SubDivision, IReadOnlyList<GroundCutMember> Members, int HolesUncut);

    private sealed class CutTally
    {
        public List<CutPiece> Made { get; } = [];

        public int Unclosed { get; set; }

        public int Declined { get; set; }

        public int BatchesSplit { get; set; }
    }
}
