using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The work behind "Prepare for Handover", with no dialog: what it would remove (<see cref="Plan"/>)
/// and the one transaction that removes it and leaves the credit (<see cref="Run"/>).
/// <see cref="PrepareForHandoverCommand"/> asks between the two; the real-Revit harness
/// (<c>revit/tools/timing</c>) calls them unattended, so the handover a curator runs is the one measured.
/// </summary>
/// <remarks>
/// Every decision is <see cref="OdblContent.PlanHandover"/>'s and every word is
/// <see cref="HandoverSummary"/>'s; this finds the containers, deletes and writes. It works from the
/// containers alone (<see cref="OdblContainers"/>), so it needs no bundle and works on a project a
/// colleague saved.
/// </remarks>
internal static class PrepareForHandover
{
    /// <summary>A container and the ids it still holds that are elements of the document.</summary>
    internal sealed record Container(SelectionFilterElement Filter, ContainerKey Key, List<ElementId> Live);

    /// <summary>What a handover of the open project would do, decided and counted before anything is deleted.</summary>
    internal sealed record HandoverPlan(IReadOnlyList<Container> Containers, IReadOnlyList<Container> Removing, HandoverCounts Counts);

    /// <summary>
    /// What <see cref="Run"/> did: whether Revit committed it, every id <c>Delete</c> removed (the filed
    /// elements and whatever went with them), the credit line, and, when it did not commit, why.
    /// </summary>
    internal sealed record HandoverResult(bool Committed, IReadOnlyCollection<ElementId> Deleted, string? CreditLine, string? Failure);

    /// <summary>The containers, what the handover keeps, removes or swaps, and the counts the dialog states.</summary>
    internal static HandoverPlan Plan(Document document)
    {
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
        return new HandoverPlan(containers, removing, counts);
    }

    /// <summary>
    /// One transaction, so one undo step, and it saves nothing: deletes what <paramref name="plan"/>
    /// removes, then the emptied containers, then makes sure the credit is in the project.
    /// </summary>
    /// <remarks>
    /// Deleting an element deletes what hangs on it (a toposolid's subdivisions, a tree hosted on it),
    /// which is why the result carries every id Revit removed, not only those that were filed.
    /// </remarks>
    internal static HandoverResult Run(Document document, HandoverPlan plan)
    {
        ImportFailureSwallower swallower = new("Preparing for handover");
        using Transaction transaction = new(document, "Mantle Place: prepare for handover");
        transaction.Start();
        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(swallower);
        options.SetForcedModalHandling(false);
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);

        List<ElementId> deleted = [];
        string creditLine;
        try
        {
            if (plan.Removing.Count > 0)
            {
                // The elements first, then the containers, which are empty by then and would otherwise
                // go on labelling content that is no longer there.
                HashSet<ElementId> doomed = [.. plan.Removing.SelectMany(container => container.Live)];
                if (doomed.Count > 0)
                {
                    deleted.AddRange(document.Delete(doomed));
                }

                document.Delete(plan.Removing.Select(container => container.Filter.Id).ToList());
            }

            creditLine = EnsureCredit(document);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            transaction.RollBack();
            return new HandoverResult(false, [], null, ex.Message);
        }

        if (transaction.Commit() != TransactionStatus.Committed)
        {
            string failure = swallower.Lines.Count > 0
                ? string.Join(" ", swallower.Lines)
                : "Revit did not accept the change, so nothing in the project was changed.";
            return new HandoverResult(false, [], null, failure);
        }

        return new HandoverResult(true, deleted, creditLine, null);
    }

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
    /// <remarks>
    /// The credit is the one the attribution step wrote, from the provenance record: this does not
    /// work out what a licence requires, it makes sure the manifest's own sources are in the "Mantle
    /// Place Attribution" view (<see cref="AttributionView.Decide"/>), adding them when the view or the
    /// note was removed. Views, materials and plans the import made stay: they hold no ODbL data once
    /// its elements are gone.
    /// </remarks>
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
