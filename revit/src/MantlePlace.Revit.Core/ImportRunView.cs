namespace MantlePlace.Revit.Core;

/// <summary>One step's row in the import window: which step, and where it stands.</summary>
public readonly record struct ImportRunRow(ImportStepKind Kind, ImportStepState State);

/// <summary>
/// What the import window shows of a <see cref="StagedImport"/>, copied at one moment. Pure and
/// immutable.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A copy, because the window is on another thread.</b> The run belongs to Revit's thread, which
/// is the only one that may touch the document, and it changes the run in every slice. The window
/// used to share that thread and read the run directly, which is why it froze with Revit through
/// every long commit. It now has a thread of its own, so it may still be drawing one of these while
/// Revit's thread runs the next slice: Revit's thread takes the copy and posts it, and the window
/// reads nothing else of the run.
/// </para>
/// <para>
/// The clock is not in it. The window measures how long the step has run from the moment a view with
/// a new <see cref="CurrentIndex"/> reaches it, so its clock keeps moving while Revit's thread cannot
/// post anything at all.
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
        bool cancelRequested)
    {
        Rows = rows;
        CurrentIndex = currentIndex;
        Progress = progress;
        Committing = committing;
        Notices = notices;
        CancelRequested = cancelRequested;
    }

    /// <summary>Every chosen step, in plan order.</summary>
    public IReadOnlyList<ImportRunRow> Rows { get; }

    /// <summary>The row of the step in flight, or -1 between steps and once the run is over.</summary>
    public int CurrentIndex { get; }

    /// <summary>The step in flight, or <c>null</c> when none is.</summary>
    public ImportStepKind? Current => CurrentIndex >= 0 ? Rows[CurrentIndex].Kind : null;

    /// <summary>How far the step in flight has got, or <c>null</c> for a step that has reported no chunk.</summary>
    public StepProgress? Progress { get; }

    /// <summary>Whether Revit is inside a commit of the step in flight (<see cref="StagedStep.Committing"/>).</summary>
    public bool Committing { get; }

    /// <summary>What the step in flight has said about its wait (<see cref="StagedStep.Notices"/>); empty when none is in flight.</summary>
    public IReadOnlyList<string> Notices { get; }

    /// <summary>Whether a cancel is waiting for its boundary.</summary>
    public bool CancelRequested { get; }

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
            current?.Committing ?? false,
            current is null ? [] : [.. current.Notices],
            run.CancelRequested);
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
        => new(Rows, CurrentIndex, Progress, Committing, Notices, cancelRequested: true);

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
