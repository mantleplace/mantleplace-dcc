namespace MantlePlace.Core;

public readonly record struct ImportRunRow(ImportStepKind Kind, ImportStepState State, string? Name = null)
{
    public string StepName => Name ?? WindowLabels.StepName(Kind);

    public static ImportRunRow Named(string name, ImportStepState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new ImportRunRow(default, state, name);
    }
}

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

    public IReadOnlyList<ImportRunRow> Rows { get; }

    public int CurrentIndex { get; }

    public ImportStepKind? Current => CurrentIndex >= 0 ? Rows[CurrentIndex].Kind : null;

    public string? CurrentName => CurrentIndex >= 0 ? Rows[CurrentIndex].StepName : null;

    public bool CurrentIsSharedStep => CurrentIndex >= 0 && Rows[CurrentIndex].Name is null;

    public StepProgress? Progress { get; }

    public bool Committing { get; }

    public IReadOnlyList<string> Notices { get; }

    public bool CancelRequested { get; }

    public bool Finishing { get; }

    public bool Indeterminate => Current is not null
        ? Progress is not { Total: > 0 } progress || (Committing && progress.IsComplete)
        : Finishing;

    private int ClockedPhase => CurrentIndex >= 0 ? CurrentIndex : Finishing ? Rows.Count : -1;

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

    public static ImportRunView From(
        IReadOnlyList<ImportRunRow> rows,
        int currentIndex,
        bool committing = false,
        StepProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(currentIndex, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(currentIndex, rows.Count);

        return new ImportRunView(
            [.. rows],
            currentIndex,
            currentIndex >= 0 ? progress : null,
            currentIndex >= 0 && committing,
            [],
            cancelRequested: false,
            finishing: false);
    }

    public static bool RestartsClock(ImportRunView? shown, ImportRunView arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);
        return (shown?.ClockedPhase ?? -1) != arriving.ClockedPhase;
    }

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
