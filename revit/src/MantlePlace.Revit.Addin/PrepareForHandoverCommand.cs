using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// "Prepare for Handover": makes the open project safe to give to a client. It removes the ODbL
/// content an import filed in its container, keeps a Produced Work terrain of the same order where
/// there is one (a swap), and leaves the data credit in the project.
/// </summary>
/// <remarks>
/// <para>
/// Every decision is <see cref="OdblContent.PlanHandover"/>'s and every word is
/// <see cref="HandoverSummary"/>'s; this finds the containers, deletes and writes. It works from the
/// containers alone (<see cref="OdblContainers"/>), so it needs no bundle and works on a project a
/// colleague saved.
/// </para>
/// <para>
/// One transaction, so one undo step, and it saves nothing. It asks first, because it deletes.
/// Deleting an element deletes what hangs on it (a toposolid's subdivisions, a tree hosted on it),
/// which is why the result counts what Revit removed as well as what was filed.
/// </para>
/// <para>
/// The credit is the one the attribution step wrote, from the provenance record: the command does not
/// work out what a licence requires, it makes sure the manifest's own sources are in the "Mantle Place
/// Attribution" view (<see cref="AttributionView.Decide"/>), adding them when the view or the note was
/// removed. Views, materials and plans the import made stay: they hold no ODbL data once its elements
/// are gone.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class PrepareForHandoverCommand : IExternalCommand
{
    private const string Title = "Mantle Place: Prepare for Handover";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        Document? document = commandData.Application.ActiveUIDocument?.Document;
        if (document is null || document.IsFamilyDocument)
        {
            new TaskDialog(Title)
            {
                MainInstruction = "Open a project first.",
                MainContent = "Prepare for Handover works on the open project.",
            }.Show();
            return Result.Cancelled;
        }

        // A container's live ids: what it still holds. An element the curator already deleted drops
        // out of a selection filter by itself, and one deleted a moment ago in this session may not
        // have, so the document is asked.
        List<Container> containers =
        [
            .. OdblContainers.All(document).Select(found => new Container(
                found.Filter,
                found.Key,
                [.. found.Filter.GetElementIds().Where(id => document.GetElement(id) is not null)])),
        ];

        HashSet<ElementId> contained = [.. containers.SelectMany(container => container.Live)];
        List<HandoverItem> items =
        [
            .. containers.Select(container => new HandoverItem(container.Key.Identity, IsOdbl: true, container.Key.Group)),
            .. ProducedWorkGrounds(document, contained),
        ];
        IReadOnlyList<HandoverDecision> decisions = OdblContent.PlanHandover(items);

        List<Container> removing = [.. containers.Where((_, index) => decisions[index] != HandoverDecision.Keep)];
        HandoverCounts counts = new(
            removing.Count,
            decisions.Take(containers.Count).Count(decision => decision == HandoverDecision.Swap),
            removing.Sum(container => container.Live.Count));

        TaskDialog ask = new(Title)
        {
            MainInstruction = "Prepare this project for handover?",
            MainContent = HandoverSummary.Ask(counts),
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        if (ask.Show() != TaskDialogResult.Yes)
        {
            return Result.Cancelled;
        }

        ImportFailureSwallower swallower = new("Preparing for handover");
        using Transaction transaction = new(document, "Mantle Place: prepare for handover");
        transaction.Start();
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(swallower);
        options.SetForcedModalHandling(false);
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);

        int deleted = 0;
        string creditLine;
        try
        {
            if (removing.Count > 0)
            {
                // The elements first, then the containers, which are empty by then and would otherwise
                // go on labelling content that is no longer there.
                HashSet<ElementId> doomed = [.. removing.SelectMany(container => container.Live)];
                deleted = doomed.Count > 0 ? document.Delete(doomed).Count : 0;
                document.Delete(removing.Select(container => container.Filter.Id).ToList());
            }

            creditLine = EnsureCredit(document);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            transaction.RollBack();
            message = ex.Message;
            return Result.Failed;
        }

        if (transaction.Commit() != TransactionStatus.Committed)
        {
            message = swallower.Lines.Count > 0
                ? string.Join(" ", swallower.Lines)
                : "Revit did not accept the change, so nothing in the project was changed.";
            return Result.Failed;
        }

        new TaskDialog(Title)
        {
            MainInstruction = "This project is prepared for handover.",
            MainContent = HandoverSummary.Done(counts, deleted, creditLine),
        }.Show();
        return Result.Succeeded;
    }

    private sealed record Container(SelectionFilterElement Filter, ContainerKey Key, List<ElementId> Live);

    /// <summary>
    /// Every ground toposolid made from a Produced Work (not in a container) whose stamp names its
    /// order: the terrain an ODbL terrain of the same order is swapped for.
    /// </summary>
    private static IEnumerable<HandoverItem> ProducedWorkGrounds(Document document, HashSet<ElementId> contained)
    {
        List<Toposolid> toposolids = [.. new FilteredElementCollector(document).OfClass(typeof(Toposolid)).Cast<Toposolid>()];
        HashSet<ElementId> subdivisions = [.. toposolids.SelectMany(toposolid => toposolid.GetSubDivisionIds())];

        foreach (Toposolid ground in toposolids.Where(toposolid => !subdivisions.Contains(toposolid.Id) && !contained.Contains(toposolid.Id)))
        {
            string? comments = ground.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            if (TerrainIdentity.StemOf(comments) is { } stem)
            {
                yield return new HandoverItem(stem, IsOdbl: false, OdblContent.TerrainGroup);
            }
        }
    }

    /// <summary>
    /// Makes sure the credit is in the project, from the provenance record, and says what it did.
    /// </summary>
    private static string EnsureCredit(Document document)
    {
        Schema schema = AttributionNotes.ProvenanceSchema();
        if (AttributionNotes.ReadProvenance(document, schema) is not { } provenance)
        {
            return HandoverSummary.NoRecord;
        }

        (string line, string recordedText) = AttributionNotes.WriteAttributionView(
            document,
            provenance,
            AttributionNotes.ReadRecorded(document, schema));
        AttributionNotes.WriteProvenance(document, schema, provenance, recordedText);
        return line;
    }
}
