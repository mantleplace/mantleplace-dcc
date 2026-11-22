using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct StepProgress(int Done, int Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0.0, 1.0) : 0.0;

    public bool IsComplete => Done >= Total;
}

public enum ImportStepState
{
    Waiting,

    Importing,

    Done,

    Failed,

    Cancelled,

    NotRun,
}

public sealed class StagedStep(ImportStep step)
{
    private readonly List<string> _notices = [];

    public ImportStep Step { get; } = step;

    public ImportStepState State { get; internal set; } = ImportStepState.Waiting;

    public StepProgress? Progress { get; internal set; }

    public bool Committing { get; internal set; }

    public IReadOnlyList<string> Notices => _notices;

    internal void Announce(string notice) => _notices.Add(notice);
}

public interface IImportStepRunner
{
    IEnumerable<StepProgress> Run(ImportStep step);

    bool Committed(ImportStep step);

    bool IsStepFailure(Exception exception);

    void StepStarting(ImportStep step);

    void StepEnded(ImportStep step, ImportStepState state, Exception? failure);
}

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

    public bool CancelRequested { get; private set; }

    public bool Finishing { get; private set; }

    internal bool FinishCommitting { get; private set; }

    private readonly List<string> _finishNotices = [];

    internal IReadOnlyList<string> FinishNotices => _finishNotices;

    public bool WasCancelled { get; private set; }

    public StagedStep? Current
        => _current >= 0 && Steps[_current].State == ImportStepState.Importing ? Steps[_current] : null;

    public string? Outcome => IsFinished && WasCancelled ? DescribeCancel() : null;

    public void RequestCancel() => CancelRequested = true;

    public void CommitStarted() => SetCommitting(true);

    public void CommitEnded() => SetCommitting(false);

    public void ReportProgress(StepProgress progress)
    {
        if (Current is { } staged)
        {
            staged.Progress = progress;
        }
    }

    public void FinishStarted()
    {
        if (IsFinished)
        {
            Finishing = true;
        }
    }

    public void FinishEnded()
    {
        Finishing = false;
        FinishCommitting = false;
        _finishNotices.Clear();
    }

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

    public void RunToEnd()
    {
        while (Advance())
        {
        }
    }

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
            EndStep(staged, ImportStepState.Failed, ex);
            return true;
        }
        catch
        {
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

        staged.Committing = false;
        staged.State = state;
        _runner.StepEnded(staged.Step, state, failure);
    }
}
