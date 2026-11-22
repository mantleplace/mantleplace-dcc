using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed class ImportFailureSwallower : IFailuresPreprocessor
{
    private readonly Dictionary<ImportFailureKind, int> _swallowed = [];
    private readonly Dictionary<ImportFailureKind, (string Caption, int Count)> _resolved = [];
    private readonly Dictionary<string, (string Text, int Count)> _unknown = new(StringComparer.Ordinal);
    private readonly List<PostedError> _errors = [];
    private readonly List<PostedError> _deleted = [];
    private readonly string _stepLabel;

    internal ImportFailureSwallower(string stepLabel) => _stepLabel = stepLabel;

    internal bool SawError => _errors.Count > 0;

    internal string FirstErrorText => _errors.Count > 0 ? _errors[0].Text : string.Empty;

    internal IReadOnlyList<PostedError> Errors => _errors;

    internal HashSet<ElementId> OwnNewElements { get; } = [];

    internal IReadOnlyList<PostedError> Deleted => _deleted;

    internal bool SawTooThin { get; private set; }

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
                lines.Add(ImportFailurePolicy.ExplainRollBack(_stepLabel, FirstErrorText, _errors[0].Id));
            }

            return lines;
        }
    }

    internal void Reset()
    {
        _swallowed.Clear();
        _resolved.Clear();
        _unknown.Clear();
        _errors.Clear();
        _deleted.Clear();
        SawTooThin = false;
    }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        => Process(failuresAccessor);

    private FailureProcessingResult Process(FailuresAccessor failuresAccessor)
    {
        ArgumentNullException.ThrowIfNull(failuresAccessor);

        List<(FailureMessageAccessor Message, ImportFailureKind Kind, string Id, string Text, ImportFailureAction Action, string Caption, IReadOnlyCollection<ElementId> Failing)> read = [];

        foreach (FailureMessageAccessor message in failuresAccessor.GetFailureMessages())
        {
            FailureDefinitionId id = message.GetFailureDefinitionId();
            ImportFailureKind kind = Classify(id);
            bool isError = message.GetSeverity() != FailureSeverity.Warning;
            bool hasResolutions = message.HasResolutions();
            IReadOnlyCollection<ElementId> failing = [.. message.GetFailingElementIds()];
            bool namesOnlyOwnNewElements = failing.Count > 0 && failing.All(OwnNewElements.Contains);

            read.Add((
                message,
                kind,
                id.Guid.ToString(),
                message.GetDescriptionText(),
                ImportFailurePolicy.Decide(kind, isError, hasResolutions, namesOnlyOwnNewElements),
                hasResolutions ? message.GetDefaultResolutionCaption() : string.Empty,
                failing));
        }

        bool deletedNow = false;
        foreach ((FailureMessageAccessor message, ImportFailureKind kind, string id, string text, ImportFailureAction action, string caption, IReadOnlyCollection<ElementId> failing) in read)
        {
            switch (action)
            {
                case ImportFailureAction.Swallow:
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

                case ImportFailureAction.DeleteNamedElements:
                    failuresAccessor.DeleteElements([.. failing]);
                    OwnNewElements.ExceptWith(failing);
                    _deleted.Add(new PostedError(id, text, failing));
                    deletedNow = true;
                    break;

                default:
                    SawTooThin |= kind is ImportFailureKind.SlabShapeTooThin or ImportFailureKind.SlabShapeEditFailed;
                    _errors.Add(new PostedError(id, text, failing));
                    break;
            }
        }

        if (SawError)
        {
            ClearAfterRollback(failuresAccessor);
            return FailureProcessingResult.ProceedWithRollBack;
        }

        return _resolved.Count > 0 || deletedNow
            ? FailureProcessingResult.ProceedWithCommit
            : FailureProcessingResult.Continue;
    }

    private static void ClearAfterRollback(FailuresAccessor failuresAccessor)
    {
        try
        {
            FailureHandlingOptions options = failuresAccessor.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            failuresAccessor.SetFailureHandlingOptions(options);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    internal void OnFailuresProcessing(object? sender, Autodesk.Revit.DB.Events.FailuresProcessingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        FailuresAccessor accessor = e.GetFailuresAccessor();
        FailureProcessingResult result = Process(accessor);

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

    private static readonly FailureDefinitionId SubDivisionRefused = new(new Guid("07338aaa-c5fe-4aa0-91e1-fa0569a8fe76"));

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

        if (id == SubDivisionRefused)
        {
            return ImportFailureKind.SubDivisionRefused;
        }

        return ImportFailureKind.Unknown;
    }
}

internal sealed record PostedError(string Id, string Text, IReadOnlyCollection<ElementId> FailingElementIds);
