namespace MantlePlace.Revit.Core;

/// <summary>One step's row in the import window: which step, and where it stands.</summary>
public readonly record struct ImportRunRow(ImportStepKind Kind, ImportStepState State);

/// <summary>
/// What the import window shows of a <see cref="StagedImport"/>, copied at one moment. Pure and
/// immutable.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A copy, because the window is on a thread of its own</b> (<c>revit/CLAUDE.md</c> says why).
/// The run belongs to Revit's thread and changes in every slice, so Revit's thread takes this copy and
/// posts it, and the window reads nothing else of the run.
/// </para>
/// <para>
/// The clock is not in it. The window measures the step's time itself, restarting when
/// <see cref="RestartsClock"/> says, so its clock keeps moving while Revit's thread posts nothing.
/// </para>
/// </remarks>
public sealed class ImportRunView
{
    private ImportRunView(
        IReadOnlyList<ImportRunRow> rows,
        int currentIndex,
        StepProgress? progress,
        bool committing,
        IReadOnlyList<string> notices,
        bool cancelRequested,
        bool finishing)
    {
        Rows = rows;
        CurrentIndex = currentIndex;
        Progress = progress;
        Committing = committing;
        Notices = notices;
        CancelRequested = cancelRequested;
        Finishing = finishing;
    }

    /// <summary>Every chosen step, in plan order.</summary>
    public IReadOnlyList<ImportRunRow> Rows { get; }

    /// <summary>The row of the step in flight, or -1 between steps and once the run is over.</summary>
    public int CurrentIndex { get; }

    /// <summary>The step in flight, or <c>null</c> when none is.</summary>
    public ImportStepKind? Current => CurrentIndex >= 0 ? Rows[CurrentIndex].Kind : null;

    /// <summary>How far the step in flight has got, or <c>null</c> for a step that has counted nothing.</summary>
    public StepProgress? Progress { get; }

    /// <summary>
    /// Whether Revit is inside a commit of the step in flight (<see cref="StagedStep.Committing"/>), or
    /// of the work after the last step.
    /// </summary>
    public bool Committing { get; }

    /// <summary>
    /// What the step in flight, or the work after the last step, has said about its wait
    /// (<see cref="StagedStep.Notices"/>); empty between steps.
    /// </summary>
    public IReadOnlyList<string> Notices { get; }

    /// <summary>Whether a cancel is waiting for its boundary.</summary>
    public bool CancelRequested { get; }

    /// <summary>Whether the work after the last step is under way (<see cref="StagedImport.Finishing"/>).</summary>
    public bool Finishing { get; }

    /// <summary>
    /// Whether the bar says "working" rather than a fraction: for a step that has counted nothing, or
    /// counted nothing to do, for one whose every counted element is now Revit's to commit, and for
    /// the work after the last step.
    /// </summary>
    /// <remarks>
    /// A chunk's commit keeps the bar on its count: the trees count after each chunk commits, so the
    /// count is short of the total during every commit but the last, and switching to "working" for a
    /// few seconds per chunk would only flicker.
    /// </remarks>
    public bool Indeterminate => Current is not null
        ? Progress is not { Total: > 0 } progress || (Committing && progress.IsComplete)
        : Finishing;

    /// <summary>
    /// Which wait the clock is timing: the step in flight's row, one past the last row for the work
    /// after the last step, or -1 for none.
    /// </summary>
    private int ClockedPhase => CurrentIndex >= 0 ? CurrentIndex : Finishing ? Rows.Count : -1;

    /// <summary>Copies what the window shows of <paramref name="run"/> now.</summary>
    public static ImportRunView Of(StagedImport run)
    {
        ArgumentNullException.ThrowIfNull(run);

        ImportRunRow[] rows = [.. run.Steps.Select(step => new ImportRunRow(step.Step.Kind, step.State))];
        StagedStep? current = run.Current;
        int currentIndex = current is null ? -1 : IndexOf(run.Steps, current);

        return new ImportRunView(
            rows,
            currentIndex,
            current?.Progress,
            current?.Committing ?? (run.Finishing && run.FinishCommitting),
            current is not null ? [.. current.Notices] : run.Finishing ? [.. run.FinishNotices] : [],
            run.CancelRequested,
            current is null && run.Finishing);
    }

    /// <summary>
    /// Whether the window's clock starts again when <paramref name="arriving"/> replaces the view it
    /// shows: when it names a different wait — the next step, the work after the last, or none.
    /// </summary>
    /// <param name="shown">The view the window shows now, or <c>null</c> for none yet.</param>
    /// <param name="arriving">The view Revit's thread has just posted.</param>
    public static bool RestartsClock(ImportRunView? shown, ImportRunView arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);
        return (shown?.ClockedPhase ?? -1) != arriving.ClockedPhase;
    }

    /// <summary>
    /// This view with a cancel waiting — what the window shows from the moment Cancel is pressed.
    /// </summary>
    /// <remarks>
    /// The cancel itself goes to Revit's thread, which hears it only when its commit returns. Until
    /// the next view arrives the window says the cancel is waiting on the view it already has, and a
    /// view that has heard it says the same.
    /// </remarks>
    public ImportRunView WithCancelRequested()
        => new(Rows, CurrentIndex, Progress, Committing, Notices, cancelRequested: true, Finishing);

    private static int IndexOf(IReadOnlyList<StagedStep> steps, StagedStep step)
    {
        for (int index = 0; index < steps.Count; index++)
        {
            if (ReferenceEquals(steps[index], step))
            {
                return index;
            }
        }

        return -1;
    }
}
