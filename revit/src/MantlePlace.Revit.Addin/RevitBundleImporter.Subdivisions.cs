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

        int created = 0;
        int declined = 0;
        int unclosed = 0;
        int unstamped = 0;

        int holesUncut = 0;

        Dictionary<ElementId, int> cutIndexOf = [];
        Dictionary<ElementId, (int HolesUncut, bool Unstamped)> countedFor = [];

        int typedForDrape = 0;
        TimeSpan typingTime = TimeSpan.Zero;

        if (SlowStepNotice.For(step.Kind, _terrainVertexCount, newBoundaries.Count) is { } notice)
        {
            Announce(notice);
        }

        bool smoothed = TerrainSmoothing.SettleBeforeCuts(step.DrapePlanned, newBoundaries.Count)
            && EnsureSmoothedSurface();

        bool typingAnnounced = false;

        ImportFailureSwallower swallower = new($"Importing the {label}");
        using Transaction transaction = BeginTransaction($"Mantle Place: {label}", swallower);

        int handled = 0;
        foreach (NewSiteBoundary boundary in newBoundaries)
        {
            Count(handled++, newBoundaries.Count);
            GroundCut cut = cuts[boundary.Ordinal - 1];
            if (Loop(cut.Outer) is not { } outer)
            {
                unclosed++;
                continue;
            }

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

            Toposolid? made = null;
            try
            {
                Toposolid subdivision = terrain.CreateSubDivision(_document, loops);
                created++;
                cutIndexOf[subdivision.Id] = boundary.Ordinal - 1;

                holesUncut += uncut;

                _createdSubDivisionIds.Add(subdivision.Id);

                Parameter? comments = subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                bool stampRefused = comments is null || comments.IsReadOnly || !comments.Set(boundary.Stamp);
                if (stampRefused)
                {
                    unstamped++;
                }

                countedFor[subdivision.Id] = (uncut, stampRefused);

                _stampsCutWith[subdivision.Id] = boundary.Stamp;
                made = subdivision;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                declined++;
            }

            if (made is null)
            {
                continue;
            }

            long before = Stopwatch.GetTimestamp();
            SubDivisionMaterialRoute route = RouteAtCut(made, out ToposolidType? typeAsCut);
            if (!typingAnnounced
                && SlowStepNotice.ForTypesAtCut(layer, route, step.DrapePlanned, newBoundaries.Count, _terrainVertexCount) is { } typing)
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

        Count(newBoundaries.Count, newBoundaries.Count);

        swallower.OwnNewElements.UnionWith(cutIndexOf.Keys);

        if (typedForDrape > 0)
        {
            Trace($"  cut: {typedForDrape:N0} of {cutIndexOf.Count:N0} subdivision(s) given their drape type "
                + $"as they were cut, in {typingTime.TotalSeconds:N1} s.");
        }

        if (!CommitAndReport(transaction, swallower))
        {
            ReportRefusedCuts(swallower, cuts, cutIndexOf);
            return;
        }

        foreach (PostedError refusal in swallower.Deleted)
        {
            foreach (ElementId id in refusal.FailingElementIds)
            {
                int index = cutIndexOf[id];
                (int uncut, bool stampRefused) = countedFor[id];
                created--;
                declined++;
                holesUncut -= uncut;
                unstamped -= stampRefused ? 1 : 0;
                _createdSubDivisionIds.Remove(id);
                _stampsCutWith.Remove(id);
                Say(ImportFailurePolicy.ExplainRefusedSubDivision(
                    index + 1,
                    FeatureName(cuts[index].Outer, "(unnamed)"),
                    refusal.Text,
                    refusal.Id,
                    id.ToString()));
            }
        }

        string summary = $"Imported {created:N0} {words.Noun} subdivision(s) from {step.EntryName}";
        if (alreadyPresent > 0)
        {
            summary += $"; {alreadyPresent:N0} from an earlier import of this bundle were already present and left alone";
        }

        summary += FidelityCuts.LevelClause(step.Level, leveled.LeftOut, leveled.KeptAboveLevel, "subdivisions");

        if (declined > 0)
        {
            summary += $"; Revit declined {declined:N0} that did not lie cleanly on the terrain";
        }

        if (unclosed > 0)
        {
            summary += $"; {unclosed:N0} could not be closed into a loop and were left out";
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

    private void ReportRefusedCuts(ImportFailureSwallower swallower, IReadOnlyList<GroundCut> cuts, Dictionary<ElementId, int> cutIndexOf)
    {
        foreach (PostedError error in swallower.Errors)
        {
            foreach (ElementId id in error.FailingElementIds)
            {
                Say(cutIndexOf.TryGetValue(id, out int index)
                    ? ImportFailurePolicy.ExplainRefusalNamedFeature(
                        error.Id, index + 1, FeatureName(cuts[index].Outer, "(unnamed)"), id.ToString())
                    : ImportFailurePolicy.ExplainRefusalNamedOther(error.Id, id.ToString()));
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
                stamps.Add(stamp);
            }
        }

        return stamps;
    }
}
