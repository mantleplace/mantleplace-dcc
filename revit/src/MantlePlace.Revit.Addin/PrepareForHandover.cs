using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal static class PrepareForHandover
{
    internal sealed record Container(SelectionFilterElement Filter, ContainerKey Key, List<ElementId> Live);

    internal sealed record HandoverPlan(IReadOnlyList<Container> Containers, IReadOnlyList<Container> Removing, HandoverCounts Counts);

    internal sealed record HandoverResult(bool Committed, IReadOnlyCollection<ElementId> Deleted, string? CreditLine, string? Failure);

    internal static HandoverPlan Plan(Document document)
    {
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
