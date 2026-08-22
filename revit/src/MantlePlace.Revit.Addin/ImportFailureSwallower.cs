using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Stands between Revit's failure machinery and the curator for the length of one import step.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Before this existed the plugin set no <c>FailureHandlingOptions</c> at all, on any of its
/// transactions. The first real import therefore ended in Revit's own modal dialog listing one error
/// and eight warnings, and because the error was one Revit will not let you ignore, the only button
/// was Cancel. That is not a dialog a curator can act on, and under
/// <c>MANTLEPLACE_BUNDLE_ZIP</c> — where nobody is watching — it is a dialog nobody dismisses.
/// </para>
/// <para>
/// The classification is <see cref="ImportFailurePolicy"/>'s, in the pure core, where it can be
/// asserted headlessly (<c>HPS-02</c>). What lives here is only the <c>FailuresAccessor</c> plumbing.
/// </para>
/// <para>
/// One instance per transaction. <see cref="Lines"/> is what the importer appends to its log, and it
/// is never empty when something was absorbed — an import that suppresses a dialog and says nothing
/// has moved the problem, not solved it.
/// </para>
/// </remarks>
internal sealed class ImportFailureSwallower : IFailuresPreprocessor
{
    private readonly Dictionary<ImportFailureKind, int> _swallowed = [];
    private readonly Dictionary<string, (string Text, int Count)> _unknown = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];
    private readonly string _stepLabel;

    internal ImportFailureSwallower(string stepLabel) => _stepLabel = stepLabel;

    /// <summary>True when an error rolled the transaction back rather than a warning being absorbed.</summary>
    internal bool SawError => _errors.Count > 0;

    /// <summary>Revit's own text for the first error, verbatim — the only string a curator can search for.</summary>
    internal string FirstErrorText => _errors.Count > 0 ? _errors[0] : string.Empty;

    /// <summary>
    /// Whether the error that rolled this back was the too-thin one, which the caller can retry
    /// against a different base plane.
    /// </summary>
    internal bool SawTooThin { get; private set; }

    /// <summary>Everything worth telling the curator, ready to append to the import log.</summary>
    internal IReadOnlyList<string> Lines
    {
        get
        {
            List<string> lines = [];
            foreach (KeyValuePair<ImportFailureKind, int> pair in _swallowed)
            {
                lines.Add(ImportFailurePolicy.Explain(pair.Key, pair.Value));
            }

            foreach (KeyValuePair<string, (string Text, int Count)> pair in _unknown)
            {
                lines.Add(ImportFailurePolicy.ExplainUnknownWarning(pair.Key, pair.Value.Text, pair.Value.Count));
            }

            if (SawError)
            {
                lines.Add(ImportFailurePolicy.ExplainRollBack(_stepLabel, FirstErrorText));
            }

            return lines;
        }
    }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        ArgumentNullException.ThrowIfNull(failuresAccessor);

        foreach (FailureMessageAccessor message in failuresAccessor.GetFailureMessages())
        {
            FailureDefinitionId id = message.GetFailureDefinitionId();
            ImportFailureKind kind = Classify(id);
            bool isError = message.GetSeverity() != FailureSeverity.Warning;

            if (ImportFailurePolicy.Decide(isError) == ImportFailureAction.RollBack)
            {
                SawTooThin |= kind is ImportFailureKind.SlabShapeTooThin or ImportFailureKind.SlabShapeEditFailed;
                _errors.Add(message.GetDescriptionText());
                continue;
            }

            // ⛔ DeleteWarning is warnings-only — Revit throws if it is handed an error — so the
            // severity test above is load-bearing, not defensive.
            failuresAccessor.DeleteWarning(message);

            if (kind == ImportFailureKind.Unknown)
            {
                string key = id.Guid.ToString();
                (string Text, int Count) seen = _unknown.TryGetValue(key, out (string Text, int Count) held)
                    ? held
                    : (message.GetDescriptionText(), 0);
                _unknown[key] = (seen.Text, seen.Count + 1);
            }
            else
            {
                _swallowed[kind] = _swallowed.TryGetValue(kind, out int count) ? count + 1 : 1;
            }
        }

        // ⛔ Continue with an unresolved error hands it back to Revit's default handler, which IS the
        // dead-end modal. And ResolveFailure is worse than either: Revit's own resolution for these
        // ids is to delete the toposolid, so the import would "succeed" with nothing in it — the
        // silently-incomplete outcome RevitBundleImporter.Execute's default arm already refuses on
        // principle.
        return SawError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }

    /// <summary>
    /// Maps a Revit failure id to the pure core's vocabulary.
    /// </summary>
    /// <remarks>
    /// <see cref="ImportFailureKind.Unknown"/> is a real answer, not a gap: the policy decides on
    /// severity alone, so an unmapped id is still handled correctly — it just gets reported verbatim
    /// instead of in our own words, which is what lets the next session add the case without a second
    /// Revit run.
    /// </remarks>
    private static ImportFailureKind Classify(FailureDefinitionId id)
    {
        if (id == BuiltInFailures.OverlapFailures.ToposolidFloorOverlap)
        {
            return ImportFailureKind.ToposolidFloorOverlap;
        }

        if (id == BuiltInFailures.OverlapFailures.ToposolidOverlap)
        {
            return ImportFailureKind.ToposolidOverlap;
        }

        if (id == BuiltInFailures.OverlapFailures.ToposolidSubregionOverlap)
        {
            return ImportFailureKind.ToposolidSubregionOverlap;
        }

        if (id == BuiltInFailures.SlabShapeFailures.SlabShapeWarnVerticesCoincident)
        {
            return ImportFailureKind.SlabShapeVerticesCoincident;
        }

        if (id == BuiltInFailures.SlabShapeFailures.SlabShapeWarnVerticesDeleted)
        {
            return ImportFailureKind.SlabShapeVerticesDeleted;
        }

        if (id == BuiltInFailures.SiteFailures.ToposolidSlopeExceedsThreshold)
        {
            return ImportFailureKind.ToposolidSlopeExceedsThreshold;
        }

        if (id == BuiltInFailures.SlabShapeFailures.SlabShapeFailedTooThin)
        {
            return ImportFailureKind.SlabShapeTooThin;
        }

        if (id == BuiltInFailures.SlabShapeFailures.SlabShapeEditFailed
            || id == BuiltInFailures.SlabShapeFailures.SlabShapeEditFailedError)
        {
            return ImportFailureKind.SlabShapeEditFailed;
        }

        return ImportFailureKind.Unknown;
    }
}
