namespace MantlePlace.Core;

public enum ImportWindowPhase
{
    Choosing,

    Begun,

    Running,

    Finished,
}

public enum ImportWindowCancel
{
    Nothing,

    CloseWindow,

    AskCancel,
}

public enum ImportWindowClose
{
    Go,

    GoAndDismiss,

    StayAndCancel,

    Stay,
}

public enum ImportWindowFinish
{
    Ignored,

    Shown,

    Close,
}

public readonly record struct ImportWindowRow(string Step, string State);

public sealed class ImportWindowState
{
    private readonly bool _offersChecklist;
    private readonly string _hostName;
    private readonly IReadOnlyDictionary<ImportStepKind, TimeSpan>? _measured;

    private ImportRunView? _view;
    private int _rowCount = -1;
    private bool _cancelAsked;
    private bool _closeWhenFinished;
    private TimeSpan? _clockStart;
    private TimeSpan? _clockStop;

    public ImportWindowState(bool offersChecklist, string hostName, IReadOnlyDictionary<ImportStepKind, TimeSpan>? measured = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);

        _offersChecklist = offersChecklist;
        _hostName = hostName;
        _measured = measured;
        Phase = offersChecklist ? ImportWindowPhase.Choosing : ImportWindowPhase.Begun;
    }

    public ImportWindowPhase Phase { get; private set; }

    public bool HasBegun => Phase != ImportWindowPhase.Choosing;

    public bool ImportVisible => _offersChecklist && Phase is ImportWindowPhase.Choosing or ImportWindowPhase.Begun;

    public bool ChecklistEditable => Phase == ImportWindowPhase.Choosing;

    public bool CancelEnabled => Phase != ImportWindowPhase.Finished && !_cancelAsked;

    public bool CloseVisible => Phase is ImportWindowPhase.Running or ImportWindowPhase.Finished;

    public bool CloseEnabled => Phase == ImportWindowPhase.Finished;

    public bool ShowsFeedbackAsk => Phase == ImportWindowPhase.Finished && !_closeWhenFinished;

    public IReadOnlyList<ImportWindowRow> Rows => _view is null
        ? []
        : [.. _view.Rows.Select(row => new ImportWindowRow(row.StepName, WindowLabels.StateWord(row.State)))];

    public ImportRunView? View => _view;

    public bool ImportEnabled(bool checklistAllowsImport) => Phase == ImportWindowPhase.Choosing && checklistAllowsImport;

    public bool PressImport(bool checklistAllowsImport)
    {
        if (!ImportEnabled(checklistAllowsImport))
        {
            return false;
        }

        Phase = ImportWindowPhase.Begun;
        return true;
    }

    public ImportWindowCancel PressCancel()
    {
        if (Phase == ImportWindowPhase.Choosing)
        {
            return ImportWindowCancel.CloseWindow;
        }

        if (Phase == ImportWindowPhase.Finished || _cancelAsked)
        {
            return ImportWindowCancel.Nothing;
        }

        _cancelAsked = true;
        return ImportWindowCancel.AskCancel;
    }

    public ImportWindowClose Closing()
    {
        switch (Phase)
        {
            case ImportWindowPhase.Finished:
                return ImportWindowClose.Go;
            case ImportWindowPhase.Choosing:
                Phase = ImportWindowPhase.Finished;
                return ImportWindowClose.GoAndDismiss;
            default:
                _closeWhenFinished = true;
                return PressCancel() == ImportWindowCancel.AskCancel ? ImportWindowClose.StayAndCancel : ImportWindowClose.Stay;
        }
    }

    public bool ShowRun(ImportRunView view, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (Phase == ImportWindowPhase.Finished)
        {
            return false;
        }

        Phase = ImportWindowPhase.Running;
        _rowCount = view.Rows.Count;
        return Refresh(view, now);
    }

    public bool Refresh(ImportRunView view, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (Phase != ImportWindowPhase.Running || view.Rows.Count != _rowCount)
        {
            return false;
        }

        if (ImportRunView.RestartsClock(_view, view))
        {
            _clockStart = now;
        }

        _view = view;
        return true;
    }

    public ImportWindowFinish Finish(ImportRunView? view, TimeSpan now)
    {
        if (Phase == ImportWindowPhase.Finished)
        {
            return ImportWindowFinish.Ignored;
        }

        if (view is not null)
        {
            Refresh(view, now);
        }

        Phase = ImportWindowPhase.Finished;
        _clockStop = now;
        return _closeWhenFinished ? ImportWindowFinish.Close : ImportWindowFinish.Shown;
    }

    public void CloseNow() => Phase = ImportWindowPhase.Finished;

    public TimeSpan Elapsed(TimeSpan now)
        => _clockStart is { } start ? (_clockStop ?? now) - start : TimeSpan.Zero;

    public string? StatusLine(TimeSpan now)
    {
        if (_view is not { } view || Phase == ImportWindowPhase.Finished)
        {
            return null;
        }

        TimeSpan? estimate = view.CurrentIsSharedStep && view.Current is { } kind && _measured is not null && _measured.TryGetValue(kind, out TimeSpan cost)
            ? cost
            : null;
        return WindowLabels.StatusLine(_cancelAsked ? view.WithCancelRequested() : view, Elapsed(now), _hostName, estimate);
    }
}
