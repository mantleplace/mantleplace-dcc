using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// How far through its work a step is, in the elements it handles: counted at each chunk's commit, or
/// by the step itself inside its one slice (<see cref="StagedImport.ReportProgress"/>).
/// </summary>
/// <param name="Done">Elements handled so far, counting the chunk that has just committed.</param>
/// <param name="Total">Elements the step has to handle in all.</param>
public readonly record struct StepProgress(int Done, int Total)
{
    /// <summary>How much of the step is done, from 0 to 1 — what a progress bar is set to.</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0.0, 1.0) : 0.0;

    /// <summary>Whether every element the step had to handle is in.</summary>
    public bool IsComplete => Done >= Total;
}

/// <summary>Where one step of a staged import stands.</summary>
public enum ImportStepState
{
    /// <summary>Not reached yet.</summary>
    Waiting,

    /// <summary>Started, and not finished.</summary>
    Importing,

    /// <summary>Ran to the end, and its work stood.</summary>
    Done,

    /// <summary>Threw, or Revit rolled its work back. The steps after it still run.</summary>
    Failed,

    /// <summary>Stopped between two chunks by a cancel. The chunks before it are kept.</summary>
    Cancelled,

    /// <summary>Never started, because the import was cancelled first.</summary>
    NotRun,
}

/// <summary>One row of a staged import: the step, and where it stands.</summary>
public sealed class StagedStep(ImportStep step)
{
    private readonly List<string> _notices = [];

    public ImportStep Step { get; } = step;

    public ImportStepState State { get; internal set; } = ImportStepState.Waiting;

    /// <summary>The last count of how far it has got, or <c>null</c> for a step that has reported none.</summary>
    public StepProgress? Progress { get; internal set; }

    /// <summary>
    /// Whether Revit is inside one of this step's commits — where it answers nothing until the commit
    /// is over. Never true once the step has ended, however it ended.
    /// </summary>
    public bool Committing { get; internal set; }

    /// <summary>What this step said about its own wait before it, in the order it said it (<see cref="SlowStepNotice"/>).</summary>
    public IReadOnlyList<string> Notices => _notices;

    internal void Announce(string notice) => _notices.Add(notice);
}

/// <summary>
/// What a staged import needs from its host: how to run a step, and what to do either side of it.
/// </summary>
/// <remarks>
/// The Revit shim implements this, and the headless suite implements it with no Revit at all — which
/// is the point. Everything about <em>when</em> a step runs, when a cancel is honoured and what a
/// failure costs is decided in <see cref="StagedImport"/>, where it can be asserted (<c>HPS-02</c>).
/// </remarks>
public interface IImportStepRunner
{
    /// <summary>
    /// The step's work. A step that commits in chunks yields once after each chunk's commit, and each
    /// yield is a point where the host gets its message loop back. A step that is one commit yields
    /// nothing.
    /// </summary>
    IEnumerable<StepProgress> Run(ImportStep step);

    /// <summary>Whether the step that has just run to the end left its work in the project.</summary>
    bool Committed(ImportStep step);

    /// <summary>
    /// Whether <paramref name="exception"/> costs only the step that threw it. Anything else stops
    /// the import.
    /// </summary>
    bool IsStepFailure(Exception exception);

    /// <summary>
    /// The slice before a step runs: the host marks the log, so a step that hangs is the last one
    /// named.
    /// </summary>
    void StepStarting(ImportStep step);

    /// <summary>
    /// A step has reached its end state. <paramref name="failure"/> is the step's own exception when
    /// <see cref="IsStepFailure"/> accepted one, and <c>null</c> otherwise — including for a step
    /// that is <see cref="ImportStepState.NotRun"/> after having been announced.
    /// </summary>
    void StepEnded(ImportStep step, ImportStepState state, Exception? failure);
}

/// <summary>
/// A bundle import run as a sequence of short slices rather than one long call. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Revit's document is reachable only from its own thread, and a handler that runs the whole import
/// in one go holds that thread for minutes: nothing repaints and nothing can be clicked. So the host
/// calls <see cref="Advance"/> once per <c>ExternalEvent</c> raise, and each call does one slice —
/// starting a step, running a step, or committing one chunk of a step — and returns. Revit repaints,
/// processes a click on Cancel, and raises again.
/// </para>
/// <para>
/// A commit cannot yield, so a step that is one commit is one slice however long it takes. What this
/// buys is everything between commits.
/// </para>
/// </remarks>
public sealed class StagedImport
{
    private readonly IImportStepRunner _runner;
    private int _current = -1;
    private IEnumerator<StepProgress>? _work;

    public StagedImport(IReadOnlyList<ImportStep> steps, IImportStepRunner runner)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        Steps = [.. steps.Select(step => new StagedStep(step))];
    }

    public IReadOnlyList<StagedStep> Steps { get; }

    public bool IsFinished { get; private set; }

    /// <summary>Whether a cancel has been asked for. It takes effect at the start of the next slice.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>
    /// Whether the host is doing the work that follows the last step — smooth shading the terrain when
    /// the drape did not settle it. No row is in flight, and Revit can still be busy for minutes.
    /// </summary>
    public bool Finishing { get; private set; }

    /// <summary>Whether Revit is inside a commit of that work.</summary>
    internal bool FinishCommitting { get; private set; }

    private readonly List<string> _finishNotices = [];

    /// <summary>What the work after the last step has said about its wait, while it is under way.</summary>
    internal IReadOnlyList<string> FinishNotices => _finishNotices;

    /// <summary>
    /// Whether a cancel actually stopped something. A cancel that arrives once every step has ended
    /// has nothing to stop, and the run is reported as the finished run it is.
    /// </summary>
    public bool WasCancelled { get; private set; }

    /// <summary>The step in flight, or <c>null</c> between steps and once the run is over.</summary>
    public StagedStep? Current
        => _current >= 0 && Steps[_current].State == ImportStepState.Importing ? Steps[_current] : null;

    /// <summary>
    /// The line the log gets about how a cancelled run ended, or <c>null</c> for one that was not
    /// cancelled.
    /// </summary>
    /// <remarks>
    /// Null on a run that ended by itself, so an import nobody cancelled writes the log it always
    /// wrote: each step already says what it did, and a closing roll-call would repeat it.
    /// </remarks>
    public string? Outcome => IsFinished && WasCancelled ? DescribeCancel() : null;

    /// <summary>
    /// Asks the import to stop at the next slice boundary: between two steps, or between two chunks
    /// of one. Nothing already committed is undone — a cancelled step's committed chunks are stamped
    /// work, and a re-import of the same build reuses them.
    /// </summary>
    public void RequestCancel() => CancelRequested = true;

    /// <summary>
    /// The host is about to commit a transaction for the step in flight, or for the work after the last
    /// step (<see cref="Finishing"/>). Nothing otherwise.
    /// </summary>
    /// <remarks>
    /// Said from inside the step's own slice, so the window can be told before Revit stops answering
    /// rather than after. A commit cannot yield (<see cref="IImportStepRunner.Run"/>), so this is the
    /// last word the window gets until it returns.
    /// </remarks>
    public void CommitStarted() => SetCommitting(true);

    /// <summary>The commit <see cref="CommitStarted"/> announced has returned, whether or not it stood.</summary>
    public void CommitEnded() => SetCommitting(false);

    /// <summary>
    /// How far the step in flight has got inside its one slice, counted by the step itself: the drape
    /// giving the photograph to each subdivision in turn, a polygon layer cutting each of its
    /// subdivisions. Nothing when no step is in flight.
    /// </summary>
    /// <remarks>
    /// A slice cannot yield inside an open transaction (<see cref="IImportStepRunner.Run"/>), so a step
    /// that does all its work in one cannot be sliced for Cancel. It can still say how far it has got,
    /// and the window shows the count beside the step's clock.
    /// </remarks>
    public void ReportProgress(StepProgress progress)
    {
        if (Current is { } staged)
        {
            staged.Progress = progress;
        }
    }

    /// <summary>The host starts the work after the last step. Nothing while a step is still to run.</summary>
    public void FinishStarted()
    {
        if (IsFinished)
        {
            Finishing = true;
        }
    }

    /// <summary>The work after the last step is over, however it ended.</summary>
    public void FinishEnded()
    {
        Finishing = false;
        FinishCommitting = false;
        _finishNotices.Clear();
    }

    /// <summary>
    /// Keeps what the step in flight — or the work after the last step — has just told the curator
    /// about its wait, for the window to show beside its clock. Nothing between steps.
    /// </summary>
    public void Announce(string notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (Current is { } staged)
        {
            staged.Announce(notice);
        }
        else if (Finishing)
        {
            _finishNotices.Add(notice);
        }
    }

    /// <summary>Marks a commit on the step in flight or, after the last step, on the finishing work.</summary>
    private void SetCommitting(bool committing)
    {
        if (Current is { } staged)
        {
            staged.Committing = committing;
        }
        else if (Finishing)
        {
            FinishCommitting = committing;
        }
    }

    /// <summary>Runs every remaining slice now, for a caller with no message loop to give back.</summary>
    public void RunToEnd()
    {
        while (Advance())
        {
        }
    }

    /// <summary>Does one slice of the import.</summary>
    /// <returns><c>true</c> while there is more to do.</returns>
    public bool Advance()
    {
        if (IsFinished)
        {
            return false;
        }

        if (CancelRequested)
        {
            StopForCancel();
            return false;
        }

        if (Current is not { } staged)
        {
            return StartNextStep();
        }

        RunSlice(staged);
        return true;
    }

    /// <summary>Runs the step in flight to its next chunk boundary, or to its end.</summary>
    /// <returns><c>true</c> when the step ended in this slice.</returns>
    private bool RunSlice(StagedStep staged)
    {
        try
        {
            _work ??= _runner.Run(staged.Step).GetEnumerator();
            if (_work.MoveNext())
            {
                staged.Progress = _work.Current;
                return false;
            }
        }
        catch (Exception ex) when (_runner.IsStepFailure(ex))
        {
            // One step's failure used to abandon every step after it. The steps take a transaction
            // each so a late failure keeps the earlier work; that promise is only half kept if the
            // LATER work is what disappears instead.
            EndStep(staged, ImportStepState.Failed, ex);
            return true;
        }
        catch
        {
            // Anything else — above all the shim's refusal of a step kind it cannot dispatch — stops
            // the import, and the run is left saying so rather than half-open.
            EndStep(staged, ImportStepState.Failed, null);
            MarkWaitingNotRun();
            IsFinished = true;
            throw;
        }

        EndStep(staged, _runner.Committed(staged.Step) ? ImportStepState.Done : ImportStepState.Failed, null);
        return true;
    }

    private bool StartNextStep()
    {
        int next = _current + 1;
        if (next >= Steps.Count)
        {
            IsFinished = true;
            return false;
        }

        _current = next;
        Steps[next].State = ImportStepState.Importing;
        _runner.StepStarting(Steps[next].Step);
        return true;
    }

    /// <summary>
    /// Ends the run where it stands. A step caught between two chunks is <see cref="ImportStepState.Cancelled"/>;
    /// one that was announced but never ran, and every step after it, is <see cref="ImportStepState.NotRun"/>.
    /// </summary>
    /// <remarks>
    /// A step whose last chunk has already committed is not stopped: all that is left of it is its
    /// closing line, and cutting it off there would report a finished layer as "stopped partway" and
    /// lose the line that says what it made. It runs to its end first, in this slice.
    /// </remarks>
    private void StopForCancel()
    {
        if (Current is { } staged)
        {
            WasCancelled = true;
            if (_work is null)
            {
                EndStep(staged, ImportStepState.NotRun, null);
            }
            else if (staged.Progress is { IsComplete: true })
            {
                while (!RunSlice(staged))
                {
                }
            }
            else
            {
                EndStep(staged, ImportStepState.Cancelled, null);
            }
        }

        WasCancelled |= Steps.Any(step => step.State == ImportStepState.Waiting);
        MarkWaitingNotRun();
        IsFinished = true;
    }

    private void MarkWaitingNotRun()
    {
        foreach (StagedStep waiting in Steps.Where(step => step.State == ImportStepState.Waiting))
        {
            waiting.State = ImportStepState.NotRun;
        }
    }

    /// <summary>
    /// Which steps completed, which failed, which were stopped with their finished part kept, and
    /// which never ran — the record a curator reads to decide whether to import again.
    /// </summary>
    private string DescribeCancel()
    {
        int reached = Steps.Count(step => step.State != ImportStepState.NotRun);
        string text = string.Format(
            CultureInfo.InvariantCulture,
            "Cancelled after {0} of {1} steps.",
            reached,
            Steps.Count);

        text += Clause("Completed", ImportStepState.Done);
        text += Clause("Failed", ImportStepState.Failed);
        text += Clause("Stopped partway, keeping what was finished", ImportStepState.Cancelled);
        text += Clause("Not run", ImportStepState.NotRun);
        return text;
    }

    private string Clause(string heading, ImportStepState state)
    {
        string[] names = [.. Steps.Where(step => step.State == state).Select(NameWithProgress)];
        return names.Length == 0 ? string.Empty : $" {heading}: {string.Join(", ", names)}.";
    }

    /// <summary>A step's name, with how far it got when it was stopped partway.</summary>
    private static string NameWithProgress(StagedStep step)
    {
        string name = WindowLabels.StepName(step.Step.Kind);
        return step.State == ImportStepState.Cancelled && step.Progress is { } progress
            ? $"{name} ({WindowLabels.ProgressText(progress)})"
            : name;
    }

    private void EndStep(StagedStep staged, ImportStepState state, Exception? failure)
    {
        _work?.Dispose();
        _work = null;

        // A commit that threw can end the step before the host has said the commit is over. An
        // ended step is never left reading as inside one, whatever the host managed to say.
        staged.Committing = false;
        staged.State = state;
        _runner.StepEnded(staged.Step, state, failure);
    }
}
