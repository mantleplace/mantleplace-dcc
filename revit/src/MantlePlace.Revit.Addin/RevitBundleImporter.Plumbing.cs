// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// What every step shares: the log, the transaction that will not stop for a dialog, finding the
// ground, and the metre conversion.
internal sealed partial class RevitBundleImporter
{
    /// <summary>Adds a line to the summary, and streams it out as it happens.</summary>
    private void Say(string line)
    {
        _log.Add(line);
        Trace(line);
    }

    /// <summary>The same, for a batch the swallower already worded.</summary>
    private void SayAll(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Say(line);
        }
    }

    /// <summary>
    /// Diagnostics only — the streamed log, never the curator's dialog. Best-effort by contract:
    /// a sink that throws must not take the import down with it.
    /// </summary>
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

    private static double InternalToMetres(double internalUnits)
        => UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Meters);

    /// <summary>
    /// Opens a transaction that will not stop for a dialog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ Every transaction this importer opens goes through here, not just the terrain one. Any of
    /// them can post a warning — the site-boundary step posts one per overlapping ring, the drape's
    /// <c>ChangeTypeId</c> can post a slope warning — and a run driven by
    /// <c>MANTLEPLACE_BUNDLE_ZIP</c> has nobody to dismiss it. Uniformity is the only way to
    /// guarantee that.
    /// </para>
    /// <para>
    /// <c>SetClearAfterRollback(true)</c> matters on the retry path: without it a rolled-back
    /// transaction leaves its failures posted, and the next attempt starts in a document Revit
    /// already considers to be in failure mode.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Commits, appends whatever the swallower absorbed to the log, and says whether it stood.
    /// </summary>
    /// <remarks>
    /// ⛔ The return value of <c>Transaction.Commit</c> used to be discarded at every one of these
    /// sites. That is how the first real import ended up reading <c>terrain.Id</c> off an element
    /// Revit had just rolled back: the step reported success, remembered a dead <c>ElementId</c>, and
    /// the drape and boundary steps then chased it.
    /// </remarks>
    private bool CommitAndReport(Transaction transaction, ImportFailureSwallower swallower)
    {
        // Timed separately from the step that owns it. Revit does its element-relation bookkeeping
        // at commit, not at the API call, so "the step took N seconds" and "the commit took N
        // seconds" point at completely different levers — and only the second one was ever the
        // problem for the site-boundary subdivisions.
        Stopwatch clock = Stopwatch.StartNew();
        TransactionStatus status = transaction.Commit();
        clock.Stop();
        Trace($"[{transaction.GetName()}] commit took {clock.Elapsed.TotalSeconds:N1} s ({status}).");

        SayAll(swallower.Lines);

        if (status == TransactionStatus.Committed)
        {
            return true;
        }

        if (!swallower.SawError)
        {
            // Rolled back with nothing posted. Rare, and worth saying so rather than reporting a
            // silent success.
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

    /// <summary>
    /// Every toposolid that is a TERRAIN — a subdivision is itself a <see cref="Toposolid"/>, so a
    /// bare collector hands back site-limit patches alongside the ground they sit on. Anything
    /// listed in another toposolid's <c>GetSubDivisionIds()</c> is excluded.
    /// </summary>
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

    /// <summary>
    /// The ground a step works on when this run did not build one — a bundle whose plan carries
    /// boundaries or a drape but no surface.
    /// </summary>
    /// <remarks>
    /// This bundle's own stamped terrain first. A project can legitimately hold more than one ground
    /// — a curator's, an adjacent order's — and "whichever the collector enumerated first" is a coin
    /// toss dressed as a lookup. The unstamped fallback stays for the project whose terrain predates
    /// stamping, where a coin toss with one coin is the right answer.
    /// </remarks>
    private ElementId TerrainToposolidId()
    {
        List<Toposolid> grounds = GroundToposolids();
        string stem = _archive.Layout.Key.Stem;

        Toposolid? mine = grounds.FirstOrDefault(ground => TerrainIdentity.IsStampFor(
            ground.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
            stem));

        return (mine ?? grounds.FirstOrDefault())?.Id ?? ElementId.InvalidElementId;
    }
}
