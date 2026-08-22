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
/// One instance per transaction, plus one for the session (see <see cref="RevitBundleImporter"/>).
/// <see cref="Lines"/> is what the importer appends to its log, and it is never empty when something
/// was absorbed — an import that suppresses a dialog and says nothing has moved the problem, not
/// solved it.
/// </para>
/// </remarks>
internal sealed class ImportFailureSwallower : IFailuresPreprocessor
{
    private readonly Dictionary<ImportFailureKind, int> _swallowed = [];
    private readonly Dictionary<ImportFailureKind, (string Caption, int Count)> _resolved = [];
    private readonly Dictionary<string, (string Text, int Count)> _unknown = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];
    private readonly string _stepLabel;

    internal ImportFailureSwallower(string stepLabel) => _stepLabel = stepLabel;

    /// <summary>True when an error rolled the transaction back rather than being absorbed or resolved.</summary>
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

            foreach (KeyValuePair<ImportFailureKind, (string Caption, int Count)> pair in _resolved)
            {
                lines.Add(ImportFailurePolicy.ExplainResolved(pair.Key, pair.Value.Caption, pair.Value.Count));
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

    /// <summary>Forgets everything absorbed so far, so one instance can serve a retry.</summary>
    internal void Reset()
    {
        _swallowed.Clear();
        _resolved.Clear();
        _unknown.Clear();
        _errors.Clear();
        SawTooThin = false;
    }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        => Process(failuresAccessor);

    /// <summary>
    /// The whole policy, in one place, so the per-transaction preprocessor and the session-wide
    /// handler cannot drift apart.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Every accessor is read fully BEFORE anything is deleted or resolved.</b> That is not
    /// tidiness — it is the fix for a real crash. Deleting a warning invalidates its
    /// <c>FailureMessageAccessor</c>, so calling <c>GetDescriptionText()</c> afterwards throws
    /// <c>InvalidOperationException: This FailureMessageAccessor has not been properly
    /// initialized</c>. An exception thrown inside a failures preprocessor makes Revit abort the
    /// transaction outright — which is exactly what happened to the site-boundary step on the first
    /// run of this code: it rolled back with no failure posted and no reason anybody could see,
    /// because the reason was our own exception.
    /// </remarks>
    private FailureProcessingResult Process(FailuresAccessor failuresAccessor)
    {
        ArgumentNullException.ThrowIfNull(failuresAccessor);

        List<(FailureMessageAccessor Message, ImportFailureKind Kind, string Id, string Text, ImportFailureAction Action, string Caption)> read = [];

        foreach (FailureMessageAccessor message in failuresAccessor.GetFailureMessages())
        {
            FailureDefinitionId id = message.GetFailureDefinitionId();
            ImportFailureKind kind = Classify(id);
            bool isError = message.GetSeverity() != FailureSeverity.Warning;
            bool hasResolutions = message.HasResolutions();

            read.Add((
                message,
                kind,
                id.Guid.ToString(),
                message.GetDescriptionText(),
                ImportFailurePolicy.Decide(kind, isError, hasResolutions),
                hasResolutions ? message.GetDefaultResolutionCaption() : string.Empty));
        }

        foreach ((FailureMessageAccessor message, ImportFailureKind kind, string id, string text, ImportFailureAction action, string caption) in read)
        {
            switch (action)
            {
                case ImportFailureAction.Swallow:
                    // ⛔ DeleteWarning is warnings-only — Revit throws if it is handed an error — so
                    // the severity test inside Decide is load-bearing, not defensive.
                    failuresAccessor.DeleteWarning(message);
                    Count(kind, id, text);
                    break;

                case ImportFailureAction.Resolve:
                    message.SetCurrentResolutionType(FailureResolutionType.Default);
                    failuresAccessor.ResolveFailure(message);
                    _resolved[kind] = _resolved.TryGetValue(kind, out (string Caption, int Count) seen)
                        ? (seen.Caption, seen.Count + 1)
                        : (caption, 1);
                    break;

                default:
                    SawTooThin |= kind is ImportFailureKind.SlabShapeTooThin or ImportFailureKind.SlabShapeEditFailed;
                    _errors.Add(text);
                    break;
            }
        }

        // ⛔ Continue with an unresolved error hands it back to Revit's default handler, which IS the
        // dead-end modal. ProceedWithCommit after resolving is what tells Revit to take the
        // resolutions we just applied rather than asking again.
        if (SawError)
        {
            return FailureProcessingResult.ProceedWithRollBack;
        }

        return _resolved.Count > 0
            ? FailureProcessingResult.ProceedWithCommit
            : FailureProcessingResult.Continue;
    }

    /// <summary>Handles failures posted by transactions this plugin did not open.</summary>
    /// <remarks>
    /// ⛔ Revit's IFC importer opens its own transaction, literally named <c>Import</c>, inside
    /// <c>RevitLinkType.CreateFromIFC</c>. A per-transaction preprocessor cannot reach it, so
    /// "Can't keep elements joined" and the IFC4 warning both surfaced as a modal in the middle of an
    /// otherwise silent import. This is subscribed to <c>Application.FailuresProcessing</c> for the
    /// duration of the import and unsubscribed in a <c>finally</c>: it is a session-wide hook, and
    /// leaving it attached would put this policy between the curator and their own edits.
    /// </remarks>
    internal void OnFailuresProcessing(object? sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        FailuresAccessor accessor = e.GetFailuresAccessor();
        FailureProcessingResult result = Process(accessor);

        // Continue means "nothing here needed me", and saying so lets Revit carry on as it would
        // have. Anything else is a decision this made and has to be handed back.
        if (result != FailureProcessingResult.Continue)
        {
            e.SetProcessingResult(result);
        }
    }

    private void Count(ImportFailureKind kind, string id, string text)
    {
        if (kind == ImportFailureKind.Unknown)
        {
            (string Text, int Count) seen = _unknown.TryGetValue(id, out (string Text, int Count) held)
                ? held
                : (text, 0);
            _unknown[id] = (seen.Text, seen.Count + 1);
            return;
        }

        _swallowed[kind] = _swallowed.TryGetValue(kind, out int count) ? count + 1 : 1;
    }

    /// <summary>
    /// Maps a Revit failure id to the pure core's vocabulary.
    /// </summary>
    /// <remarks>
    /// <see cref="ImportFailureKind.Unknown"/> is a real answer, not a gap: an unmapped id is still
    /// handled correctly because severity decides the action — it just gets reported verbatim instead
    /// of in our own words, which is what lets the next session add the case without a second Revit
    /// run. <c>InaccurateSketchLine</c> was added exactly that way.
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

        if (id == BuiltInFailures.OverlapFailures.ToposolidSubregionOverlap
            || id == BuiltInFailures.OverlapFailures.SubregionOverlap)
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

        if (id == BuiltInFailures.JoinElementsFailures.CannotKeepJoined)
        {
            return ImportFailureKind.CannotKeepElementsJoined;
        }

        if (id == BuiltInFailures.InaccurateFailures.InaccurateSketchLine)
        {
            return ImportFailureKind.InaccurateSketchLine;
        }

        return ImportFailureKind.Unknown;
    }
}
