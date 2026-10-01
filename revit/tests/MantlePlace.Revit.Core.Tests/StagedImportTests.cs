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

        run.Case("the window's status line names the step in flight, how far it has got and how long it has run", () =>
        {
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.Vegetation] = () => Chunks(runner, 250, 350);
            StagedImport import = new([Step(ImportStepKind.ToposurfaceFromSurfaceTin), Step(ImportStepKind.Vegetation)], runner);

            run.Equal(Status(import, 0), string.Empty, "nothing before the first slice");

            import.Advance();
            run.Equal(Status(import, 12), "Terrain: 12 s", "a one-commit step has no count to show, only its clock");

            import.Advance();
            import.Advance();
            import.Advance();
            run.Equal(Status(import, 65), "Planting: 250 of 600, 1 min 5 s", "a chunked step counts its elements");
            run.Within(ImportRunView.Of(import).Progress!.Value.Fraction, 250.0 / 600.0, 1e-9, "and the bar's fraction");

            import.RequestCancel();
            run.Equal(
                Status(import, 65),
                "Planting: 250 of 600, 1 min 5 s. Cancelling at the next step or chunk.",
                "a pending cancel is said until it lands");
        });

        run.Case("inside a step's commit the status line says Revit is committing, and only there", () =>
        {
            // The commit is where Revit stops answering for minutes: a road layer's single commit, the
            // drape's. The window cannot count inside it, but it can say that is where Revit is.
            FakeRunner runner = new();
            StagedImport import = null!;
            string? during = null;
            string? chunkDuring = null;
            runner.Bodies[ImportStepKind.RoadPolygons] = () => InsideCommit(() => import, () => during = Status(import, 250));
            runner.Bodies[ImportStepKind.Vegetation] = () => ChunkInsideCommit(() => import, 600, () => chunkDuring = Status(import, 65));
            import = new([Step(ImportStepKind.RoadPolygons), Step(ImportStepKind.Vegetation)], runner);

            import.Advance();
            run.Equal(Status(import, 5), "Road Subdivisions: 5 s", "before its commit, the step and its clock");

            import.Advance();
            run.Equal(during, "Road Subdivisions: Revit is committing, 4 min 10 s", "inside it, where Revit is");

            import.Advance();
            import.Advance();
            run.Equal(chunkDuring, "Planting: Revit is committing, 1 min 5 s", "a chunk's commit, before any chunk has counted");
            run.Equal(Status(import, 66), "Planting: 600 of 600, 1 min 6 s", "and once the chunk is in, the count and no commit");
        });

        run.Case("what a step says about its wait is shown while it runs, and goes when it ends", () =>
        {
            // The slow steps say, before their commit, what the wait has measured at
            // (SlowStepNotice). The log has always had it; the window shows it beside the clock that
            // is counting that very wait, and not over the step after.
            FakeRunner runner = new();
            StagedImport import = null!;
            ImportRunView? during = null;
            runner.Bodies[ImportStepKind.RoadPolygons] = () => Announcing(
                () => import,
                ["Next: the road surfaces.", "Each is typed as it is cut."],
                () => during = ImportRunView.Of(import));
            import = new([Step(ImportStepKind.RoadPolygons), Step(ImportStepKind.LinkSiteIfc)], runner);

            import.Announce("said between steps, so it is no step's");
            import.Advance();
            run.Equal(ImportRunView.Of(import).Notices.Count, 0, "nothing is said before the step has run");

            import.Advance();
            run.Equal(
                string.Join("|", during!.Notices),
                "Next: the road surfaces.|Each is typed as it is cut.",
                "what the step said, in the order it said it");

            import.Advance();
            run.Equal(ImportRunView.Of(import).CurrentIndex, 1, "the next step is in flight");
            run.Equal(ImportRunView.Of(import).Notices.Count, 0, "and it starts with nothing said");
        });

        run.Case("a cancel pressed during a commit is said at once, before Revit's thread has heard it", () =>
        {
            // Cancel is posted to Revit's thread, which is inside the commit and hears nothing until it
            // returns. The window says the cancel is waiting from the moment it is pressed, on the
            // view it already holds, rather than looking as though the click did nothing.
            FakeRunner runner = new();
            StagedImport import = null!;
            ImportRunView? during = null;
            runner.Bodies[ImportStepKind.ImageryDrape] = () => InsideCommit(() => import, () => during = ImportRunView.Of(import));
            import = new([Step(ImportStepKind.ImageryDrape)], runner);

            import.Advance();
            import.Advance();

            ImportRunView pressed = during!.WithCancelRequested();
            run.Equal(
                WindowLabels.StatusLine(pressed, TimeSpan.FromSeconds(900)),
                "Imagery Drape: Revit is committing, 15 min 0 s. Cancelling at the next step or chunk.",
                "the pending cancel, over the commit it waits for");
            run.False(during!.CancelRequested, "the view the window was handed is left as it was");
            run.False(import.CancelRequested, "and the run itself has not heard of it");
        });

        run.Case("a step that fails inside its commit leaves no commit behind for the next step", () =>
        {
            // The host says a commit has ended in a finally, but a throw from the commit itself is
            // the case that finally exists for, and the run must not depend on the host getting it
            // right: a stale "committing" would be said over the next step's whole run.
            FakeRunner runner = new();
            StagedImport import = null!;
            runner.Bodies[ImportStepKind.RoadPolygons] = () => ThrowsInsideCommit(() => import);
            import = new([Step(ImportStepKind.RoadPolygons), Step(ImportStepKind.LinkSiteIfc)], runner);

            import.Advance();
            import.Advance();
            run.True(import.Steps[0].State == ImportStepState.Failed, "the road step failed");
            run.False(import.Steps[0].Committing, "and is not left inside a commit it will never finish");

            import.Advance();
            run.Equal(Status(import, 1), "Site Model: 1 s", "and the next step is not said to be committing");
        });

        run.Case("the window's clock restarts when a view names a new step, and only then", () =>
        {
            FakeRunner runner = new();
            runner.Bodies[ImportStepKind.Vegetation] = () => Chunks(runner, 250, 350);
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc), Step(ImportStepKind.Vegetation)], runner);

            ImportRunView before = ImportRunView.Of(import);
            run.False(ImportRunView.RestartsClock(null, before), "the first view, with nothing in flight");

            import.Advance();
            ImportRunView first = ImportRunView.Of(import);
            run.True(ImportRunView.RestartsClock(before, first), "the first step starts");
            run.False(ImportRunView.RestartsClock(first, ImportRunView.Of(import)), "the same step again");

            import.Advance();
            ImportRunView between = ImportRunView.Of(import);
            run.True(ImportRunView.RestartsClock(first, between), "the step ended");

            import.Advance();
            import.Advance();
            ImportRunView chunk = ImportRunView.Of(import);
            run.True(ImportRunView.RestartsClock(between, chunk), "the next step");
            import.Advance();
            run.False(ImportRunView.RestartsClock(chunk, ImportRunView.Of(import)), "a later chunk of the same step keeps its clock");
        });

        run.Case("the work after the last step is named, clocked, and never said to be cancelling", () =>
        {
            // Smooth shading, when the drape did not settle it, is committed after every step has
            // ended: no row is in flight, and without a name the window would read every step Done,
            // an empty bar and no status while Revit hangs.
            FakeRunner runner = new();
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc)], runner);
            import.RunToEnd();
            ImportRunView over = ImportRunView.Of(import);

            import.FinishStarted();
            run.True(import.Finishing, "finishing");
            run.Equal(Status(import, 3), "Finishing: 3 s", "named, with its clock");
            run.True(ImportRunView.Of(import).Indeterminate, "and the bar says working");
            run.True(ImportRunView.RestartsClock(over, ImportRunView.Of(import)), "its clock starts as a step's does");

            import.CommitStarted();
            ImportRunView committing = ImportRunView.Of(import);
            run.Equal(Status(import, 80), "Finishing: Revit is committing, 1 min 20 s", "its commit");
            run.False(ImportRunView.RestartsClock(ImportRunView.Of(import), committing), "which keeps the clock");
            run.Equal(
                WindowLabels.StatusLine(committing.WithCancelRequested(), TimeSpan.FromSeconds(80)),
                "Finishing: Revit is committing, 1 min 20 s",
                "a cancel has nothing left to stop here");

            import.CommitEnded();
            import.FinishEnded();
            run.Equal(Status(import, 81), string.Empty, "and nothing once it is over");
            run.False(ImportRunView.Of(import).Indeterminate, "with the bar at rest");
        });

        run.Case("what the finishing work says about its wait is shown beside its clock, and goes with it", () =>
        {
            // Smooth shading committed after the last step can take minutes in Revit 2026 and 2027,
            // and it is said before the commit the way a step's wait is.
            FakeRunner runner = new();
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc)], runner);
            import.RunToEnd();

            import.FinishStarted();
            import.Announce("Next: smooth shading.");
            run.Equal(string.Join("|", ImportRunView.Of(import).Notices), "Next: smooth shading.", "shown while finishing");

            import.FinishEnded();
            run.Equal(ImportRunView.Of(import).Notices.Count, 0, "and gone once it is over");
        });

        run.Case("a run still in its steps cannot start finishing", () =>
        {
            FakeRunner runner = new();
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc)], runner);
            import.Advance();
            import.FinishStarted();
            run.False(import.Finishing, "a step is still to run");
        });

        run.Case("a step can count inside its one slice, and the bar follows the count", () =>
        {
            // The drape gives the photograph to each subdivision in turn inside one transaction, which
            // cannot yield, so it counts instead, and the window says how far it has got.
            FakeRunner runner = new();
            StagedImport import = null!;
            string? counting = null;
            bool countingIndeterminate = true;
            string? committing = null;
            bool committingIndeterminate = false;
            runner.Bodies[ImportStepKind.ImageryDrape] = () => CountingThenCommit(
                () => import,
                405,
                () =>
                {
                    counting = Status(import, 65);
                    countingIndeterminate = ImportRunView.Of(import).Indeterminate;
                },
                () =>
                {
                    committing = Status(import, 70);
                    committingIndeterminate = ImportRunView.Of(import).Indeterminate;
                });
            import = new([Step(ImportStepKind.ImageryDrape)], runner);

            import.Advance();
            run.True(ImportRunView.Of(import).Indeterminate, "a one-commit step with no count yet says working");

            import.Advance();
            run.Equal(counting, "Imagery Drape: 120 of 405, 1 min 5 s", "the count, as it goes");
            run.False(countingIndeterminate, "the bar shows the count");
            run.Equal(committing, "Imagery Drape: 405 of 405, Revit is committing, 1 min 10 s", "then Revit's commit");
            run.True(committingIndeterminate, "and the bar says working once all that was counted is Revit's to commit");
        });

        run.Case("a step that counted nothing to do says working, not an empty bar", () =>
        {
            // A re-import whose subdivisions are all on the terrain already counts 0 of 0; a bar at
            // 0% beside it would read as stuck.
            FakeRunner runner = new();
            StagedImport import = null!;
            bool indeterminate = false;
            string? status = null;
            runner.Bodies[ImportStepKind.Water] = () => Counting(() => import, () =>
            {
                indeterminate = ImportRunView.Of(import).Indeterminate;
                status = Status(import, 2);
            });
            import = new([Step(ImportStepKind.Water)], runner);

            import.Advance();
            import.Advance();
            run.True(indeterminate, "the bar says working");
            run.Equal(status, "Water Subdivisions: 2 s", "and the line counts nothing");
        });

        run.Case("a chunk's commit keeps the bar on its count", () =>
        {
            // The trees count after each chunk commits, so during a chunk's commit the count is short
            // of the total, and the bar keeps it rather than flickering to working every few seconds.
            FakeRunner runner = new();
            StagedImport import = null!;
            bool indeterminate = true;
            runner.Bodies[ImportStepKind.Vegetation] = () => ChunksCommittingInside(
                () => import,
                () => indeterminate = ImportRunView.Of(import).Indeterminate,
                250,
                350);
            import = new([Step(ImportStepKind.Vegetation)], runner);

            import.Advance();
            import.Advance();
            import.Advance();
            run.False(indeterminate, "the second chunk's commit, at 250 of 600");
        });

        run.Case("the window reads a copy of the run, which the run's next slice does not change", () =>
        {
            // The window lives on a thread of its own, so it may still be reading a view while Revit's
            // thread runs the next slice. What it holds has to stay what it was.
            FakeRunner runner = new();
            StagedImport import = new([Step(ImportStepKind.LinkSiteIfc), Step(ImportStepKind.RoadCentrelines)], runner);

            import.Advance();
            ImportRunView view = ImportRunView.Of(import);

            import.Advance();
            import.Advance();

            run.Equal(view.CurrentIndex, 0, "the view still has the first step in flight");
            run.True(view.Rows[0].State == ImportStepState.Importing, "and still reads it as importing");
            run.True(view.Rows[1].State == ImportStepState.Waiting, "with the second still waiting");
            run.Equal(WindowLabels.StatusLine(view, TimeSpan.FromSeconds(3)), "Site Model: 3 s", "and its status line is the first step's");

            ImportRunView now = ImportRunView.Of(import);
            run.Equal(now.CurrentIndex, 1, "a new view has moved on");
            run.True(now.Rows[0].State == ImportStepState.Done, "with the first step done");
        });

        return run.Report("staged import");
    }

    private static ImportStep Step(ImportStepKind kind) => new() { Kind = kind };

    private static string Joined(IEnumerable<string> events) => string.Join("|", events);

    /// <summary>The status line the window would show for <paramref name="import"/> now, its clock at <paramref name="seconds"/>.</summary>
    private static string Status(StagedImport import, double seconds)
        => WindowLabels.StatusLine(ImportRunView.Of(import), TimeSpan.FromSeconds(seconds));

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

    /// <summary>A step that is one commit, the way the polygon steps are: <paramref name="during"/> runs inside it.</summary>
    private static IEnumerable<StepProgress> InsideCommit(Func<StagedImport> import, Action during)
    {
        import().CommitStarted();
        during();
        import().CommitEnded();
        yield break;
    }

    /// <summary>One chunk of <paramref name="total"/> elements, with <paramref name="during"/> run inside its commit.</summary>
    private static IEnumerable<StepProgress> ChunkInsideCommit(Func<StagedImport> import, int total, Action during)
    {
        import().CommitStarted();
        during();
        import().CommitEnded();
        yield return new StepProgress(total, total);
    }

    /// <summary>A step that is one commit and says <paramref name="notices"/> before it, then runs <paramref name="during"/>.</summary>
    private static IEnumerable<StepProgress> Announcing(Func<StagedImport> import, string[] notices, Action during)
    {
        foreach (string notice in notices)
        {
            import().Announce(notice);
        }

        during();
        yield break;
    }

    /// <summary>
    /// A step that is one commit and counts <paramref name="total"/> elements inside its one slice,
    /// the way the drape does: <paramref name="midway"/> runs at 120, <paramref name="inCommit"/>
    /// inside the commit that follows the count.
    /// </summary>
    private static IEnumerable<StepProgress> CountingThenCommit(Func<StagedImport> import, int total, Action midway, Action inCommit)
    {
        for (int done = 0; done <= total; done++)
        {
            import().ReportProgress(new StepProgress(done, total));
            if (done == 120)
            {
                midway();
            }
        }

        import().CommitStarted();
        inCommit();
        import().CommitEnded();
        yield break;
    }

    /// <summary>A step that counts 0 of 0, then runs <paramref name="after"/> before any commit.</summary>
    private static IEnumerable<StepProgress> Counting(Func<StagedImport> import, Action after)
    {
        import().ReportProgress(new StepProgress(0, 0));
        after();
        yield break;
    }

    /// <summary>The tree step's shape: one commit per chunk, counted once the chunk is in, with <paramref name="inCommit"/> run inside each.</summary>
    private static IEnumerable<StepProgress> ChunksCommittingInside(Func<StagedImport> import, Action inCommit, params int[] sizes)
    {
        int total = sizes.Sum();
        int done = 0;
        foreach (int size in sizes)
        {
            import().CommitStarted();
            inCommit();
            import().CommitEnded();
            done += size;
            yield return new StepProgress(done, total);
        }
    }

    /// <summary>A step whose commit throws, before the host can say it has ended.</summary>
    private static IEnumerable<StepProgress> ThrowsInsideCommit(Func<StagedImport> import)
    {
        import().CommitStarted();
        throw new IOException("the commit threw");
#pragma warning disable CS0162 // An iterator needs a yield to be one.
        yield break;
#pragma warning restore CS0162
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
