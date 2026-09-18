// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The context-building step: each building's own extrusion read out of the site model and created
// in the project as a Generic Model element, in chunks, each stamped with its IFC GlobalId.
internal sealed partial class RevitBundleImporter
{
    /// <summary>What a context building is called in the project.</summary>
    private const string ContextBuildingName = "Context Building";

    /// <summary>
    /// Every building in the site model as its own Generic Model element, the context terrain left
    /// out — created in chunks, each building stamped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The site model is converted the way the link step converts it, and nothing more: no geometry
    /// is computed here. Each building's solid is the extrusion the platform published, read back out
    /// of Revit's own IFC import and handed to a <see cref="DirectShape"/> in this project. Which
    /// elements are buildings is <see cref="SiteModelReader"/>'s decision, from the IFC's text; this
    /// finds each one again by the GlobalId Revit's import records on it.
    /// </para>
    /// <para>
    /// ⛔ <b>Placed as the link was placed.</b> A link instance is created origin to origin, so the
    /// geometry is copied with no transform at all: the buildings land exactly where the link put
    /// them, and a placement computed here would be one the boundary refuses (<c>HPS-33</c>).
    /// </para>
    /// <para>
    /// ⛔ <b>The converted site model never outlives the slice that opened it.</b> Every building's
    /// solids are cloned out of it and it is closed before the first chunk, because a document held
    /// open across slices is closed only if the step ends through <see cref="StagedImport"/> — and an
    /// import abandoned from the event handler does not, which would leave an invisible document open
    /// for the rest of the session. It is never saved either: the version-qualified companion
    /// <c>.rvt</c> exists only for a link to point at (<see cref="SiteCompanionPath"/>).
    /// </para>
    /// <para>
    /// One transaction per chunk and a yield after each commit, for the trees' reason
    /// (<see cref="ImportVegetation"/>); every building carries its stamp
    /// (<see cref="BuildingIdentity"/>), so a re-import of the same build creates only what a cancel
    /// left missing, and an import where nothing is missing never converts the site model at all.
    /// </para>
    /// </remarks>
    private IEnumerable<StepProgress> ImportContextBuildings(ImportStep step)
    {
        string ifcPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
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
            ExistingBuildingComments(),
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

        if (ReadBuildingSolids(step, ifcPath, globalIds) is not { } solids)
        {
            yield break;
        }

        if (solids.Count == 0)
        {
            // One sentence rather than N "could not be found": this is Revit's import not recording
            // the GlobalId where this looks, not N buildings each going missing.
            Say($"Revit's import of the site model ({step.EntryName}) recorded no IFC GlobalId matching any of "
                + $"the {globalIds.Count:N0} building(s) the file declares, so no context building was copied.");
            yield break;
        }

        // Converting the site model was one uninterruptible call. The window gets its count before
        // the first chunk rather than after it.
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
                // The swallower already said why, and the rollback took this chunk. The chunks before
                // it stand, stamped; a re-import of this build picks up from here.
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

    /// <summary>What one chunk's transaction did.</summary>
    private readonly record struct BuildingChunkResult(bool Committed, int Created, int Unstamped, int Unbuilt);

    /// <summary>Creates, stamps and commits one chunk of context buildings.</summary>
    /// <remarks>A building Revit's import did not carry is skipped here and counted by the caller.</remarks>
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

            if (geometry.Count == 0 || TryCreateDirectShape(category, geometry, ContextBuildingName) is not { } shape)
            {
                unbuilt++;
                continue;
            }

            created++;

            // Comments is the building's identity for the NEXT import. One it could not stamp is
            // kept — it is real — and cannot be recognised later, which the summary says.
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

    /// <summary>
    /// Converts the site model, clones the solids of every building in <paramref name="globalIds"/>
    /// out of it, and closes it — or says why it could not be converted.
    /// </summary>
    /// <returns>
    /// The solids by GlobalId, holding only the buildings Revit's import carried; <c>null</c> when
    /// the conversion failed.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The same conversion the link step runs, and like it, outside any transaction on the host
    /// document. A separate method because a Revit exception has to be caught here, and an iterator
    /// cannot catch around a <c>yield</c>.
    /// </para>
    /// <para>
    /// Revit's IFC import writes an element's GlobalId to the built-in <c>IfcGUID</c> parameter. The
    /// by-name lookup is the same parameter reached another way, for an import that adds it as a
    /// plain parameter rather than the built-in one; neither is a guess about which element is a
    /// building, which was settled from the file before this ran.
    /// </para>
    /// <para>
    /// Solids only, each cloned, because nothing may reach into the site model once it is closed. A
    /// mesh has no clone to make, and the site model publishes extrusions, which Revit imports as
    /// solids; a building that came back as anything else is counted as one Revit would not accept.
    /// </para>
    /// </remarks>
    private Dictionary<string, List<GeometryObject>>? ReadBuildingSolids(
        ImportStep step,
        string ifcPath,
        IReadOnlyList<string> globalIds)
    {
        HashSet<string> wanted = new(globalIds, StringComparer.Ordinal);
        Dictionary<string, List<GeometryObject>> solids = new(StringComparer.Ordinal);
        Options options = new() { DetailLevel = ViewDetailLevel.Fine };

        Document? siteModel = null;
        try
        {
            siteModel = _application.OpenIFCDocument(ifcPath);

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

            return solids;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException)
        {
            Say($"Could not open the site model to copy its buildings ({step.EntryName}): {ex.Message}");
            return null;
        }
        finally
        {
            siteModel?.Close(false);
        }
    }

    /// <summary>Every solid in <paramref name="geometry"/>, instances included, cloned free of its document.</summary>
    /// <remarks>An instance's geometry is read in the site model's coordinates, the frame every other element is in.</remarks>
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

    /// <summary>
    /// The Comments of every DirectShape in the project. Whatever is not a building stamp is ignored
    /// by <see cref="BuildingIdentity"/>, so there is no category filter here to get wrong.
    /// </summary>
    /// <remarks>
    /// The same read the tree step makes, kept here rather than shared: the trees are due to stop
    /// being DirectShapes, and a building step that borrowed their collector would lose it with them.
    /// </remarks>
    private List<string?> ExistingBuildingComments()
    {
        using FilteredElementCollector collector = new(_document);
        return [.. collector
            .OfClass(typeof(DirectShape))
            .Select(element => element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())];
    }
}
