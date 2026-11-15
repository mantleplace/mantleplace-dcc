namespace MantlePlace.Core;

/// <summary>Where the import window stands.</summary>
public enum ImportWindowPhase
{
    /// <summary>On its checklist: nothing has run, and Import has not been pressed.</summary>
    Choosing,

    /// <summary>The run is the host's to start or already started, and its steps have not arrived.</summary>
    Begun,

    /// <summary>Showing the run's steps.</summary>
    Running,

    /// <summary>The run is over, or the window is on its way out: nothing more is taken.</summary>
    Finished,
}

/// <summary>What a press of the import window's Cancel does.</summary>
public enum ImportWindowCancel
{
    /// <summary>Nothing: the cancel is already asked, or the run is over.</summary>
    Nothing,

    /// <summary>Nothing has run: the window closes, which drops the import.</summary>
    CloseWindow,

    /// <summary>Asks the host to stop the run at its next boundary.</summary>
    AskCancel,
}

/// <summary>What the import window does when asked to close.</summary>
public enum ImportWindowClose
{
    /// <summary>It goes, asking nothing.</summary>
    Go,

    /// <summary>It goes, and the import that never began is dropped.</summary>
    GoAndDismiss,

    /// <summary>It stays until the run stops, and asks the host for the cancel.</summary>
    StayAndCancel,

    /// <summary>It stays until the run stops; the cancel is already asked.</summary>
    Stay,
}

/// <summary>What the import window does with the run's end.</summary>
public enum ImportWindowFinish
{
    /// <summary>Nothing: it had already finished.</summary>
    Ignored,

    /// <summary>It shows the report and is the curator's to close.</summary>
    Shown,

    /// <summary>It was asked to close during the run, and goes now.</summary>
    Close,
}

/// <summary>One step's row in the import window, in its words: the step's name and its state.</summary>
public readonly record struct ImportWindowRow(string Step, string State);

/// <summary>
/// The import window's state, which every .NET host's window shows and none decides: the checklist,
/// the run's steps in plan order and each one's state, Import, Cancel between steps, Close, and the
/// step's clock. Pure; not thread-safe, and owned by the window's thread.
/// </summary>
/// <remarks>
/// <para>
/// The host runs its own steps (<see cref="IImportStepRunner"/>, <see cref="StagedImport"/>, or a run
/// of its own) and posts the window <see cref="ImportRunView"/>s; this decides what the window does
/// with them and with the curator's presses, and the WPF window only lays it out.
/// </para>
/// <para>
/// <b>The clock is the window's own.</b> It restarts when a view names a different wait
/// (<see cref="ImportRunView.RestartsClock"/>), and is read against the time the window passes in, so
/// it keeps moving while the host posts nothing — through a commit that holds the host's thread.
/// </para>
/// <para>
/// <b>Closing the window while the run goes is a cancel.</b> The run is on the host's thread with no
/// other place to see or stop it, so the window stays until the run has stopped at its next boundary.
/// Closing it — or pressing Cancel — before Import has nothing to wait for, and the window goes.
/// </para>
/// </remarks>
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

    /// <param name="offersChecklist">
    /// Whether the host offers a per-layer choice. A host with none passes <c>false</c>: the window
    /// opens on its steps, already begun, with no Import to press.
    /// </param>
    /// <param name="hostName">The host's product name, which says who is committing (<see cref="WindowLabels.HostName"/>).</param>
    /// <param name="measured">
    /// What the host measured each step at, said beside the step's clock; <c>null</c> or a step absent
    /// from it says the clock alone.
    /// </param>
    public ImportWindowState(bool offersChecklist, string hostName, IReadOnlyDictionary<ImportStepKind, TimeSpan>? measured = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostName);

        _offersChecklist = offersChecklist;
        _hostName = hostName;
        _measured = measured;
        Phase = offersChecklist ? ImportWindowPhase.Choosing : ImportWindowPhase.Begun;
    }

    /// <summary>Where the window stands.</summary>
    public ImportWindowPhase Phase { get; private set; }

    /// <summary>Whether Import has been pressed, or there was no checklist to press it on.</summary>
    public bool HasBegun => Phase != ImportWindowPhase.Choosing;

    /// <summary>Whether Import shows: over a checklist, until the steps replace it.</summary>
    public bool ImportVisible => _offersChecklist && Phase is ImportWindowPhase.Choosing or ImportWindowPhase.Begun;

    /// <summary>Whether the checklist's boxes and levels can still be changed.</summary>
    public bool ChecklistEditable => Phase == ImportWindowPhase.Choosing;

    /// <summary>Whether Cancel can be pressed.</summary>
    public bool CancelEnabled => Phase != ImportWindowPhase.Finished && !_cancelAsked;

    /// <summary>Whether Close shows: once the steps do.</summary>
    public bool CloseVisible => Phase is ImportWindowPhase.Running or ImportWindowPhase.Finished;

    /// <summary>Whether Close can be pressed: once the run is over.</summary>
    public bool CloseEnabled => Phase == ImportWindowPhase.Finished;

    /// <summary>
    /// Whether the finished window may ask "Did this import come in right?": not when it is closing on
    /// its way out of a cancelled run.
    /// </summary>
    public bool ShowsFeedbackAsk => Phase == ImportWindowPhase.Finished && !_closeWhenFinished;

    /// <summary>The run's steps, in plan order, in the window's words; empty before they arrive.</summary>
    public IReadOnlyList<ImportWindowRow> Rows => _view is null
        ? []
        : [.. _view.Rows.Select(row => new ImportWindowRow(WindowLabels.StepName(row.Kind), WindowLabels.StateWord(row.State)))];

    /// <summary>The view the window shows, or <c>null</c> before the steps arrive.</summary>
    public ImportRunView? View => _view;

    /// <summary>Whether Import may be pressed now, over a checklist that does or does not allow it.</summary>
    public bool ImportEnabled(bool checklistAllowsImport) => Phase == ImportWindowPhase.Choosing && checklistAllowsImport;

    /// <summary>
    /// Import is pressed. <c>true</c> exactly once, when the window should hand the host the curator's
    /// choice; from then on a close is a cancel.
    /// </summary>
    public bool PressImport(bool checklistAllowsImport)
    {
        if (!ImportEnabled(checklistAllowsImport))
        {
            return false;
        }

        // Begun before the host is called rather than after: a second click queued behind a slow
        // re-plan would otherwise begin the same import twice.
        Phase = ImportWindowPhase.Begun;
        return true;
    }

    /// <summary>Cancel is pressed.</summary>
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

    /// <summary>The window is asked to close, by its close box, Close, or Cancel before Import.</summary>
    public ImportWindowClose Closing()
    {
        switch (Phase)
        {
            case ImportWindowPhase.Finished:
                return ImportWindowClose.Go;
            case ImportWindowPhase.Choosing:
                // Nothing has run and nothing will: the window goes now, and the import is dropped.
                Phase = ImportWindowPhase.Finished;
                return ImportWindowClose.GoAndDismiss;
            default:
                _closeWhenFinished = true;
                return PressCancel() == ImportWindowCancel.AskCancel ? ImportWindowClose.StayAndCancel : ImportWindowClose.Stay;
        }
    }

    /// <summary>The run's steps replace the checklist. <c>false</c> once the window has finished.</summary>
    /// <param name="view">The run as the host first saw it planned.</param>
    /// <param name="now">The window's clock reading, from any monotonic source.</param>
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

    /// <summary>
    /// The run as the host last saw it. <c>false</c>, and nothing changes, for a view that arrives
    /// before the steps, after the end, or naming a different set of steps.
    /// </summary>
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

    /// <summary>The run is over: the report shows, or the window that was asked to close goes.</summary>
    /// <param name="view">The run as it ended, or <c>null</c> for one that never showed its steps.</param>
    /// <param name="now">The window's clock reading, where the clock stops.</param>
    public ImportWindowFinish Finish(ImportRunView? view, TimeSpan now)
    {
        if (Phase == ImportWindowPhase.Finished)
        {
            // Dismissed before Import: the window is already on its way out.
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

    /// <summary>
    /// The window closes whatever it shows, asking for no cancel: the host is shutting down, or the
    /// project its checklist was for has closed.
    /// </summary>
    public void CloseNow() => Phase = ImportWindowPhase.Finished;

    /// <summary>How long the wait in flight has run, at <paramref name="now"/>; stopped at the end.</summary>
    public TimeSpan Elapsed(TimeSpan now)
        => _clockStart is { } start ? (_clockStop ?? now) - start : TimeSpan.Zero;

    /// <summary>
    /// The status line at <paramref name="now"/>, or <c>null</c> when the window paints none: before
    /// the steps, and once finished, when the line is the outcome's.
    /// </summary>
    /// <remarks>
    /// A cancel pressed here reaches the host's thread only when its commit returns; it is said from
    /// the moment it is pressed.
    /// </remarks>
    public string? StatusLine(TimeSpan now)
    {
        if (_view is not { } view || Phase == ImportWindowPhase.Finished)
        {
            return null;
        }

        TimeSpan? estimate = view.Current is { } kind && _measured is not null && _measured.TryGetValue(kind, out TimeSpan cost)
            ? cost
            : null;
        return WindowLabels.StatusLine(_cancelAsked ? view.WithCancelRequested() : view, Elapsed(now), _hostName, estimate);
    }
}
