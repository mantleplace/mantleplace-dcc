using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The staged import: one step, or one chunk of a step, per <see cref="StagedImport.Advance"/>, so
/// the host can hand Revit its message loop back between them.
/// </summary>
internal static class StagedImportTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("each step takes two slices — one to show it starting, one to run it", () =>
        {
            FakeRunner runner = new();
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc), Step(ImportStepKind.RoadCentrelines)], runner);

            run.True(import.Advance(), "slice 1 starts the first step");
            run.Equal(Joined(runner.Events), "start LinkSiteIfc", "nothing has run yet, so the window can show it starting");
            run.True(import.Steps[0].State == ImportStepState.Importing, "the first step reads as importing");

            run.True(import.Advance(), "slice 2 runs it");
            run.True(import.Steps[0].State == ImportStepState.Done, "and it is done");

            run.True(import.Advance(), "slice 3 starts the second");
            run.True(import.Advance(), "slice 4 runs it");
            run.False(import.Advance(), "slice 5 has nothing left");
            run.True(import.IsFinished, "the run is finished");
            run.Equal(
                Joined(runner.Events),
                "start LinkSiteIfc|run LinkSiteIfc|end LinkSiteIfc Done|start RoadCentrelines|run RoadCentrelines|end RoadCentrelines Done",
                "every step ran, in plan order");
            run.True(import.Outcome is null, "a run nobody cancelled adds no line to the log");
        });

        run.Case("a chunked step takes one slice per chunk, and says how far it has got", () =>
        {
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.Vegetation] = () => Chunks(runner, 250, 250, 100);
            StagedImport import = new([Step(ImportStepKind.Vegetation)], runner);

            import.Advance();
            run.True(import.Steps[0].Progress is null, "no progress before the first chunk");

            import.Advance();
            run.True(import.Steps[0].Progress == new StepProgress(250, 600), "after chunk 1");
            run.True(import.Steps[0].State == ImportStepState.Importing, "still importing between chunks");

            import.Advance();
            run.True(import.Steps[0].Progress == new StepProgress(500, 600), "after chunk 2");

            import.Advance();
            run.True(import.Steps[0].Progress == new StepProgress(600, 600), "after chunk 3");

            import.Advance();
            run.True(import.Steps[0].State == ImportStepState.Done, "the step ends on the slice after its last chunk");
            run.Equal(runner.ChunksCommitted, 3, "every chunk committed");
        });

        run.Case("a cancel between chunks stops the step there, and keeps what it committed", () =>
        {
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.Vegetation] = () => Chunks(runner, 250, 250, 100);
            StagedImport import = new(
                [Step(ImportStepKind.ToposurfaceFromSurfaceTin), Step(ImportStepKind.Vegetation), Step(ImportStepKind.ImageryDrape)],
                runner);

            import.Advance();
            import.Advance();
            import.Advance();
            import.Advance();
            run.Equal(runner.ChunksCommitted, 1, "one chunk of trees is in");

            import.RequestCancel();
            import.RunToEnd();

            run.Equal(runner.ChunksCommitted, 1, "no chunk runs after the cancel");
            run.True(runner.Disposed, "the step's work was abandoned cleanly rather than left suspended");
            run.True(import.Steps[0].State == ImportStepState.Done, "the terrain stays done");
            run.True(import.Steps[1].State == ImportStepState.Cancelled, "the trees read as cancelled partway");
            run.True(import.Steps[2].State == ImportStepState.NotRun, "the drape never started");
            run.False(runner.Events.Contains("start ImageryDrape"), "and was not even announced");
            run.True(runner.Events.Contains("end Vegetation Cancelled"), "the host heard the step end");
            run.True(import.IsFinished, "the run is over");
        });

        run.Case("a step that throws costs that step alone", () =>
        {
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.LinkSiteIfc] = () => throw new IOException("the IFC would not convert");
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc), Step(ImportStepKind.RoadCentrelines)], runner);

            import.RunToEnd();

            run.True(import.Steps[0].State == ImportStepState.Failed, "the link failed");
            run.True(import.Steps[1].State == ImportStepState.Done, "the roads still ran");
            run.True(
                runner.Events.Contains("end LinkSiteIfc Failed (the IFC would not convert)"),
                "the host was handed the exception, so it can say why");
        });

        run.Case("a step whose commit Revit rolled back is failed, not done", () =>
        {
            FakeRunner runner = new();
            runner.RolledBack.Add(ImportStepKind.SiteBoundaries);
            StagedImport import = new([Step(ImportStepKind.SiteBoundaries)], runner);

            import.RunToEnd();

            run.True(import.Steps[0].State == ImportStepState.Failed, "a rollback leaves nothing, so the step did not complete");
        });

        run.Case("an exception that is not a step's own stops the import", () =>
        {
            // The shim throws InvalidOperationException for a step kind this build cannot dispatch,
            // and that must stop the import rather than quietly produce a model missing a layer.
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.LinkSiteIfc] = () => throw new InvalidOperationException("unknown kind");
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc), Step(ImportStepKind.RoadCentrelines)], runner);

            import.Advance();
            bool threw = false;
            try
            {
                import.Advance();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            run.True(threw, "it reaches the host");
            run.True(import.IsFinished, "and the run is over");
            run.True(import.Steps[0].State == ImportStepState.Failed, "the step that threw failed");
            run.True(import.Steps[1].State == ImportStepState.NotRun, "nothing after it ran");
            run.False(import.Advance(), "there is nothing left to advance");
        });

        run.Case("a cancel between steps names what completed, what was kept partway, and what never ran", () =>
        {
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.Vegetation] = () => Chunks(runner, 250, 250, 100);
            runner.RolledBack.Add(ImportStepKind.RoadCentrelines);
            StagedImport import = new(
                [
                    Step(ImportStepKind.ToposurfaceFromSurfaceTin),
                    Step(ImportStepKind.RoadCentrelines),
                    Step(ImportStepKind.Vegetation),
                    Step(ImportStepKind.ImageryDrape),
                ],
                runner);

            for (int slice = 0; slice < 6; slice++)
            {
                import.Advance();
            }

            import.RequestCancel();
            import.RunToEnd();

            run.Equal(
                import.Outcome,
                "Cancelled after 3 of 4 steps. Completed: Terrain. Failed: Road Centrelines. "
                    + "Stopped partway, keeping what was finished: Planting (250 of 600). Not run: Imagery Drape.",
                "the log's closing line");
        });

        run.Case("a cancel before anything ran says so", () =>
        {
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc)], new FakeRunner());
            import.RequestCancel();
            import.RunToEnd();

            run.Equal(
                import.Outcome,
                "Cancelled after 0 of 1 steps. Not run: Site Model.",
                "nothing completed, and nothing is claimed");
        });

        run.Case("a cancel after a step's last chunk lets the step finish rather than calling it cancelled", () =>
        {
            // The last chunk has committed and only the step's closing line is left. Stopping there
            // would report "Planting (600 of 600)" as stopped partway and lose the summary.
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.Vegetation] = () => ChunksThenSay(runner, "trees said", 250, 350);
            StagedImport import = new([Step(ImportStepKind.Vegetation), Step(ImportStepKind.ImageryDrape)], runner);

            import.Advance();
            import.Advance();
            import.Advance();
            run.True(import.Steps[0].Progress == new StepProgress(600, 600), "every chunk is in");

            import.RequestCancel();
            import.RunToEnd();

            run.True(import.Steps[0].State == ImportStepState.Done, "the trees are done");
            run.True(runner.Events.Contains("trees said"), "and said so");
            run.True(import.Steps[1].State == ImportStepState.NotRun, "the drape still never started");
            run.Equal(
                import.Outcome,
                "Cancelled after 1 of 2 steps. Completed: Planting. Not run: Imagery Drape.",
                "the closing line");
        });

        run.Case("a cancel that arrives after the last step has nothing to cancel", () =>
        {
            FakeRunner runner = new();
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc)], runner);

            import.Advance();
            import.Advance();
            import.RequestCancel();
            import.RunToEnd();

            run.True(import.Steps[0].State == ImportStepState.Done, "the one step is done");
            run.False(import.WasCancelled, "a run with nothing left to stop was not cancelled");
            run.True(import.Outcome is null, "and the log says nothing about a cancel");
        });

        run.Case("the window's status line names the step in flight and how far it has got", () =>
        {
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.Vegetation] = () => Chunks(runner, 250, 350);
            StagedImport import = new([Step(ImportStepKind.ToposurfaceFromSurfaceTin), Step(ImportStepKind.Vegetation)], runner);

            run.Equal(WindowLabels.StatusLine(import), string.Empty, "nothing before the first slice");

            import.Advance();
            run.Equal(WindowLabels.StatusLine(import), "Terrain…", "a one-commit step has no count to show");

            import.Advance();
            import.Advance();
            import.Advance();
            run.Equal(WindowLabels.StatusLine(import), "Planting: 250 of 600", "a chunked step counts its elements");
            run.Within(import.Current!.Progress!.Value.Fraction, 250.0 / 600.0, 1e-9, "and the bar's fraction");

            import.RequestCancel();
            run.Equal(
                WindowLabels.StatusLine(import),
                "Planting: 250 of 600. Cancelling at the next step or chunk.",
                "a pending cancel is said until it lands");
        });

        return run.Report("staged import");
    }

    private static ImportStep Step(ImportStepKind kind) => new() { Kind = kind };

    private static string Joined(IEnumerable<string> events) => string.Join("|", events);

    /// <summary>A step that commits one chunk per size given, the way the tree step does.</summary>
    private static IEnumerable<StepProgress> Chunks(FakeRunner runner, params int[] sizes)
    {
        int total = sizes.Sum();
        int done = 0;
        try
        {
            foreach (int size in sizes)
            {
                done += size;
                runner.ChunksCommitted++;
                yield return new StepProgress(done, total);
            }
        }
        finally
        {
            runner.Disposed = true;
        }
    }

    /// <summary>As <see cref="Chunks"/>, with the closing line a real step writes after its last chunk.</summary>
    private static IEnumerable<StepProgress> ChunksThenSay(FakeRunner runner, string closing, params int[] sizes)
    {
        foreach (StepProgress progress in Chunks(runner, sizes))
        {
            yield return progress;
        }

        runner.Events.Add(closing);
    }

    /// <summary>A host with no Revit: records what it was asked to do, and does what each step's script says.</summary>
    private sealed class FakeRunner : IImportStepRunner
    {
        internal List<string> Events { get; } = [];

        internal Dictionary<ImportStepKind, Func<IEnumerable<StepProgress>>> Bodies { get; } = [];

        internal HashSet<ImportStepKind> RolledBack { get; } = [];

        internal int ChunksCommitted { get; set; }

        internal bool Disposed { get; set; }

        public IEnumerable<StepProgress> Run(ImportStep step)
        {
            Events.Add($"run {step.Kind}");
            return Bodies.TryGetValue(step.Kind, out Func<IEnumerable<StepProgress>>? body) ? body() : [];
        }

        public bool Committed(ImportStep step) => !RolledBack.Contains(step.Kind);

        public bool IsStepFailure(Exception exception) => exception is IOException;

        public void StepStarting(ImportStep step) => Events.Add($"start {step.Kind}");

        public void StepEnded(ImportStep step, ImportStepState state, Exception? failure)
            => Events.Add($"end {step.Kind} {state}" + (failure is null ? string.Empty : $" ({failure.Message})"));
    }
}
