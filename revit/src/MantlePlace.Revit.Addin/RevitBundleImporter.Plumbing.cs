using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private void Say(string line)
    {
        _log.Add(line);
        Trace(line);
    }

    private void Announce(string? notice)
    {
        if (notice is null)
        {
            return;
        }

        Say(notice);
        Announced?.Invoke(notice);
    }

    private void Count(int done, int total) => Counted?.Invoke(new StepProgress(done, total));

    private void SayAll(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Say(line);
        }
    }

    private void Trace(string line)
    {
        try
        {
            _trace?.Invoke(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static double MetresToInternal(double metres)
        => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

    internal AnchorDecision Anchoring { get; set; } = new() { Kind = AnchorDecisionKind.FirstBundle };

    internal SurveyPointPlacement? IncomingSurveyPoint { get; set; }

    private bool _anchorConfirmed;

    private XYZ SitePoint(double eastM, double northM, double z)
        => new(MetresToInternal(eastM + Anchoring.OffsetEastM), MetresToInternal(northM + Anchoring.OffsetNorthM), z);

    private void MoveToAnchor(ElementId id, string what)
    {
        if (!Anchoring.MovesGeometry || _document.GetElement(id) is not { } element)
        {
            return;
        }

        bool pinned = element.Pinned;
        try
        {
            if (pinned)
            {
                element.Pinned = false;
            }

            ElementTransformUtils.MoveElement(
                _document,
                id,
                new XYZ(MetresToInternal(Anchoring.OffsetEastM), MetresToInternal(Anchoring.OffsetNorthM), 0.0));
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            Say($"The {what} could not be moved to where this bundle stands, so it is at the project origin, on top of "
                + $"the first bundle ({ex.Message}). Move it by hand.");
        }
        finally
        {
            if (pinned)
            {
                element.Pinned = true;
            }
        }
    }

    private void ConfirmAnchor()
    {
        if (_anchorConfirmed)
        {
            return;
        }

        _anchorConfirmed = true;
        AnchorDecision now = AdjacentBundle.Decide(ProjectAnchorStore.Read(_document), IncomingSurveyPoint);
        if (now.IsRefusal)
        {
            throw new InvalidOperationException(now.Message);
        }

        if (now.Kind != Anchoring.Kind
            || Math.Abs(now.OffsetEastM - Anchoring.OffsetEastM) > 1e-6
            || Math.Abs(now.OffsetNorthM - Anchoring.OffsetNorthM) > 1e-6)
        {
            throw new InvalidOperationException(
                "This project's shared coordinates changed after this import was opened, so nothing was imported. "
                + "Open the bundle again.");
        }
    }

    private static double InternalToMetres(double internalUnits)
        => UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Meters);

    private double ShortCurveToleranceM => InternalToMetres(_document.Application.ShortCurveTolerance);

    private Transaction BeginTransaction(string name, ImportFailureSwallower swallower)
    {
        Transaction transaction = new(_document, name);
        transaction.Start();

        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(swallower);
        options.SetForcedModalHandling(false);
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);

        return transaction;
    }

    private bool CommitAndReport(Transaction transaction, ImportFailureSwallower swallower)
    {
        Stopwatch clock = Stopwatch.StartNew();
        TransactionStatus status;
        CommitStarting?.Invoke();
        try
        {
            status = transaction.Commit();
        }
        finally
        {
            clock.Stop();
            CommitEnded?.Invoke();
        }

        Trace($"[{transaction.GetName()}] commit took {clock.Elapsed.TotalSeconds:N1} s ({status}).");

        SayAll(swallower.Lines);

        if (status == TransactionStatus.Committed)
        {
            return true;
        }

        _stepRolledBack = true;
        if (!swallower.SawError)
        {
            Say($"Revit did not accept \"{transaction.GetName()}\" ({status}). Nothing from that "
                + "step was left in the project.");
        }

        return false;
    }

    private ElementId FirstElementIdOf<T>()
        where T : Element
    {
        using FilteredElementCollector collector = new(_document);
        return collector.OfClass(typeof(T)).FirstElementId();
    }

    private List<Toposolid> GroundToposolids()
    {
        List<Toposolid> toposolids = new FilteredElementCollector(_document)
            .OfClass(typeof(Toposolid))
            .Cast<Toposolid>()
            .ToList();

        HashSet<ElementId> subdivisionIds = [];
        foreach (Toposolid toposolid in toposolids)
        {
            foreach (ElementId id in toposolid.GetSubDivisionIds())
            {
                subdivisionIds.Add(id);
            }
        }

        return [.. toposolids.Where(toposolid => !subdivisionIds.Contains(toposolid.Id))];
    }

    private List<ExistingTerrain> ExistingGrounds()
        => [.. GroundToposolids().Select(ground => new ExistingTerrain(
            ground.Id.Value,
            ground.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()))];

    private bool HeightsRefused(ImportStep step)
    {
        if (step.HeightDatum is not { } content)
        {
            return false;
        }

        GroundDatum? ground = HeightDatums.GroundFor(ExistingGrounds(), _archive.Layout.Key.Stem, _terrainId.Value);
        if (HeightDatums.Refusal(ground, content, HeightDatums.Noun(step.Kind)) is not { } reason)
        {
            return false;
        }

        Say(reason);
        return true;
    }

    private ElementId TerrainToposolidId()
    {
        List<Toposolid> grounds = GroundToposolids();
        string stem = _archive.Layout.Key.Stem;

        Toposolid? mine = grounds.FirstOrDefault(ground => TerrainIdentity.IsStampFor(
            ground.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
            stem));

        return (mine ?? grounds.FirstOrDefault())?.Id ?? ElementId.InvalidElementId;
    }

    private List<string?> ExistingDirectShapeComments()
    {
        using FilteredElementCollector collector = new(_document);
        return [.. collector
            .OfClass(typeof(DirectShape))
            .Select(element => element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())];
    }
}
