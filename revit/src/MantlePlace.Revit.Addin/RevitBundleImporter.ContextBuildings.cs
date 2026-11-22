using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private const string ContextBuildingName = "Context Building";

    private IEnumerable<StepProgress> ImportContextBuildings(ImportStep step)
    {
        if (HeightsRefused(step))
        {
            yield break;
        }

        string ifcPath = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);
        if (SiteModelReader.TryRead(File.ReadAllText(ifcPath), out SiteModelContents contents) is { } readError)
        {
            Say(readError);
            yield break;
        }

        if (contents.UnidentifiedBuildings > 0)
        {
            Say($"{contents.UnidentifiedBuildings:N0} building(s) in the site model carry no IFC GlobalId, so "
                + "they could not be stamped or found again, and were not copied.");
        }

        string stem = _archive.Layout.Key.Stem;
        BuildingDecision decision = BuildingIdentity.Decide(
            ExistingDirectShapeComments(),
            stem,
            step.ExpectedSha256,
            contents.BuildingGlobalIds);
        if (decision.Disposition == BuildingDisposition.RefuseStale)
        {
            Say(decision.Explanation);
            yield break;
        }

        IReadOnlyList<string> globalIds = decision.GlobalIdsToCreate;
        if (globalIds.Count == 0)
        {
            Say(decision.AlreadyPresent > 0
                ? $"All {decision.AlreadyPresent:N0} context building(s) from {step.EntryName} were already in "
                    + "this project from an earlier import of this build, so none were copied again."
                : $"The site model ({step.EntryName}) carries no buildings, so there were none to copy.");
            yield break;
        }

        Announce(SlowStepNotice.For(step.Kind, _terrainVertexCount, globalIds.Count));

        string? companion = step.SavesLinkCompanion
            ? SiteCompanionPath.ForVersion(ifcPath, _application.VersionNumber)
            : null;
        if (companion is not null && File.Exists(companion))
        {
            companion = null;
        }

        if (ReadBuildingSolids(step, ifcPath, globalIds, companion) is not { } solids)
        {
            yield break;
        }

        if (solids.Count == 0)
        {
            Say($"Revit's import of the site model ({step.EntryName}) recorded no IFC GlobalId matching any of "
                + $"the {globalIds.Count:N0} building(s) the file declares, so no context building was copied.");
            yield break;
        }

        yield return new StepProgress(0, globalIds.Count);

        ElementId category = DirectShapeCategory(BuiltInCategory.OST_GenericModel);
        int created = 0;
        int unstamped = 0;
        int unbuilt = 0;
        foreach (ImportChunk chunk in ImportChunking.Chunks(globalIds.Count))
        {
            BuildingChunkResult result = CreateBuildingChunk(category, solids, globalIds, chunk, stem, step.ExpectedSha256);
            if (!result.Committed)
            {
                Say($"Stopped the context buildings after {created:N0} of {globalIds.Count:N0}: Revit did not "
                    + "accept a chunk.");
                yield break;
            }

            created += result.Created;
            unstamped += result.Unstamped;
            unbuilt += result.Unbuilt;
            yield return new StepProgress(chunk.Start + chunk.Count, globalIds.Count);
        }

        int notFound = globalIds.Count(globalId => !solids.ContainsKey(globalId));
        string summary = $"Copied {created:N0} context building(s) of {contents.BuildingGlobalIds.Count:N0} "
            + $"from the site model ({step.EntryName}) as Generic Model elements";
        if (decision.AlreadyPresent > 0)
        {
            summary += $"; {decision.AlreadyPresent:N0} from an earlier import of this build were already "
                + "present and left alone";
        }

        if (notFound > 0)
        {
            summary += $"; {notFound:N0} were not in Revit's import of the site model";
        }

        if (unbuilt > 0)
        {
            summary += $"; {unbuilt:N0} had geometry Revit would not accept as an element";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        if (contents.TerrainElements > 0)
        {
            summary += ". The site model's context terrain was left out — the terrain is the toposolid";
        }

        Say(summary + ".");
    }

    private readonly record struct BuildingChunkResult(bool Committed, int Created, int Unstamped, int Unbuilt);

    private BuildingChunkResult CreateBuildingChunk(
        ElementId category,
        Dictionary<string, List<GeometryObject>> solids,
        IReadOnlyList<string> globalIds,
        ImportChunk chunk,
        string stem,
        string? sha256)
    {
        int created = 0;
        int unstamped = 0;
        int unbuilt = 0;

        ImportFailureSwallower swallower = new("Copying the context buildings");
        using Transaction transaction = BeginTransaction("Mantle Place: context buildings", swallower);

        for (int index = chunk.Start; index < chunk.Start + chunk.Count; index++)
        {
            string globalId = globalIds[index];
            if (!solids.TryGetValue(globalId, out List<GeometryObject>? geometry))
            {
                continue;
            }

            if (geometry.Count == 0 || TryCreateDirectShape(category, AtAnchor(geometry), ContextBuildingName) is not { } shape)
            {
                unbuilt++;
                continue;
            }

            created++;

            Parameter? comments = shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments is null || comments.IsReadOnly || !comments.Set(BuildingIdentity.Stamp(stem, sha256, globalId)))
            {
                unstamped++;
            }
        }

        return CommitAndReport(transaction, swallower)
            ? new BuildingChunkResult(true, created, unstamped, unbuilt)
            : new BuildingChunkResult(false, 0, 0, 0);
    }

    private List<GeometryObject> AtAnchor(List<GeometryObject> geometry)
    {
        if (!Anchoring.MovesGeometry)
        {
            return geometry;
        }

        Transform move = Transform.CreateTranslation(
            new XYZ(MetresToInternal(Anchoring.OffsetEastM), MetresToInternal(Anchoring.OffsetNorthM), 0.0));
        return [.. geometry.Select(item => item is Solid solid ? SolidUtils.CreateTransformed(solid, move) : item)];
    }

    private Dictionary<string, List<GeometryObject>>? ReadBuildingSolids(
        ImportStep step,
        string ifcPath,
        IReadOnlyList<string> globalIds,
        string? companion)
    {
        HashSet<string> wanted = new(globalIds, StringComparer.Ordinal);
        Dictionary<string, List<GeometryObject>> solids = new(StringComparer.Ordinal);
        Options options = new() { DetailLevel = ViewDetailLevel.Fine };

        Document? siteModel = null;
        int errorsBefore = SessionErrorCount;
        try
        {
            Stopwatch converting = Stopwatch.StartNew();
            siteModel = OpenSiteModel(ifcPath);
            Trace($"  site model: converted {step.EntryName} in {converting.Elapsed.TotalSeconds:0.0} s, to copy its buildings.");

            using FilteredElementCollector collector = new(siteModel);
            foreach (Element element in collector.WhereElementIsNotElementType())
            {
                string? globalId = element.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString()
                    ?? element.LookupParameter("IfcGUID")?.AsString();
                if (globalId is null || !wanted.Contains(globalId) || solids.ContainsKey(globalId))
                {
                    continue;
                }

                List<GeometryObject> cloned = [];
                if (element.get_Geometry(options) is { } geometry)
                {
                    CloneSolids(geometry, cloned);
                }

                solids.Add(globalId, cloned);
            }

            if (companion is not null)
            {
                SaveLinkCompanion(siteModel, companion);
            }

            return solids;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException)
        {
            Say($"Could not open the site model to copy its buildings ({step.EntryName}): {ex.Message}");
            if (SiteModelRefusal(step.EntryName, errorsBefore, "the context buildings were not imported") is { } why)
            {
                Say(why);
            }

            return null;
        }
        finally
        {
            siteModel?.Close(false);
        }
    }

    private void SaveLinkCompanion(Document siteModel, string companion)
    {
        try
        {
            siteModel.SaveAs(companion);
            Trace($"  site model: saved the conversion as {Path.GetFileName(companion)}, so the link step will not convert it again.");
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException)
        {
            Trace($"  site model: could not save the conversion for the link ({ex.Message}); the link step will convert it again.");
        }
    }

    private static void CloneSolids(GeometryElement geometry, List<GeometryObject> into)
    {
        foreach (GeometryObject item in geometry)
        {
            switch (item)
            {
                case Solid solid when solid.Faces.Size > 0:
                    into.Add(SolidUtils.Clone(solid));
                    break;
                case GeometryInstance instance:
                    CloneSolids(instance.GetInstanceGeometry(), into);
                    break;
            }
        }
    }
}
