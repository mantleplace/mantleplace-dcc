using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

/// <summary>
/// The shared import window's state (<see cref="ImportWindowState"/>): which buttons it shows, what a
/// press or a close does at each point of a run, the steps and their order, Cancel between steps, and
/// the step's clock. The window is WPF and CI never builds it, so everything it decides is here.
/// </summary>
internal static class ImportWindowStateTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("on its checklist: Import is offered, Cancel closes, and Close is not shown", () =>
        {
            ImportWindowState state = new(offersChecklist: true, "Revit");

            run.True(state.Phase == ImportWindowPhase.Choosing, "it opens on the checklist");
            run.True(state.ImportVisible, "Import shows");
            run.True(state.ImportEnabled(checklistAllowsImport: true), "and can be pressed");
            run.False(state.ImportEnabled(checklistAllowsImport: false), "but not over a checklist that cannot import");
            run.True(state.ChecklistEditable, "the boxes and levels can be changed");
            run.True(state.CancelEnabled, "Cancel can be pressed");
            run.False(state.CloseVisible, "Close is not shown yet");
            run.Equal(state.PressCancel(), ImportWindowCancel.CloseWindow, "Cancel before Import closes the window");
        });

        run.Case("closing it on its checklist drops the import, and nothing waits", () =>
        {
            ImportWindowState state = new(offersChecklist: true, "Revit");

            run.Equal(state.Closing(), ImportWindowClose.GoAndDismiss, "the window goes and the import is dropped");
            run.True(state.Phase == ImportWindowPhase.Finished, "and the window is done");
            run.Equal(state.Closing(), ImportWindowClose.Go, "a second close only goes");
        });

        run.Case("Import begins the run once, and from then a close is a cancel", () =>
        {
            ImportWindowState state = new(offersChecklist: true, "Revit");

            run.True(state.PressImport(checklistAllowsImport: true), "the first press begins");
            run.False(state.PressImport(checklistAllowsImport: true), "a second press queued behind it begins nothing");
            run.True(state.Phase == ImportWindowPhase.Begun, "begun, before the plan's steps arrive");
            run.False(state.ImportEnabled(checklistAllowsImport: true), "Import is dark");
            run.False(state.ChecklistEditable, "and the checklist is fixed");

            run.Equal(state.Closing(), ImportWindowClose.StayAndCancel, "a close now asks for the cancel and waits");
            run.False(state.CancelEnabled, "the cancel is asked once");
        });

        run.Case("Import over a checklist that cannot import begins nothing", () =>
        {
            ImportWindowState state = new(offersChecklist: true, "Revit");
            run.False(state.PressImport(checklistAllowsImport: false), "nothing begins");
            run.True(state.Phase == ImportWindowPhase.Choosing, "and the checklist stays");
        });

        run.Case("a host with no per-layer choice opens on its steps, with no Import to press", () =>
        {
            ImportWindowState state = new(offersChecklist: false, "AutoCAD");

            run.True(state.Phase == ImportWindowPhase.Begun, "already begun: the host runs its steps at once");
            run.False(state.ImportVisible, "no Import");
            run.False(state.PressImport(checklistAllowsImport: true), "and pressing one would begin nothing");
            run.Equal(state.PressCancel(), ImportWindowCancel.AskCancel, "Cancel asks the run to stop");

            ImportRunView view = View(Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Importing)), current: 0);
            run.True(state.ShowRun(view, TimeSpan.Zero), "its steps show");
            run.Equal(Names(state), "Site Model", "the steps only");
        });

        run.Case("the run shows every chosen step, in plan order, with each one's state", () =>
        {
            ImportWindowState state = Begun();
            ImportRunView view = View(
                Rows(
                    (ImportStepKind.ToposurfaceFromSurfaceTin, ImportStepState.Done),
                    (ImportStepKind.LinkSiteIfc, ImportStepState.Importing),
                    (ImportStepKind.Vegetation, ImportStepState.Waiting)),
                current: 1);

            run.True(state.ShowRun(view, TimeSpan.Zero), "the steps replace the checklist");
            run.True(state.Phase == ImportWindowPhase.Running, "running");
            run.Equal(Names(state), "Terrain|Site Model|Planting", "in plan order");
            run.Equal(States(state), "Done|Importing|Waiting", "each with its state");
            run.False(state.ImportVisible, "Import goes");
            run.True(state.CloseVisible, "Close shows");
            run.False(state.CloseEnabled, "but is dark while the run is going");
        });

        run.Case("a view that names a different set of steps from the one shown is not taken", () =>
        {
            ImportWindowState state = Begun();
            state.ShowRun(View(Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Importing)), current: 0), TimeSpan.Zero);

            ImportRunView other = View(
                Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Done), (ImportStepKind.Vegetation, ImportStepState.Importing)),
                current: 1);
            run.False(state.Refresh(other, TimeSpan.FromSeconds(1)), "refused");
            run.Equal(States(state), "Importing", "the rows are as they were");
        });

        run.Case("a view before the steps are shown is not taken", () =>
        {
            ImportWindowState state = Begun();
            run.False(state.Refresh(View(Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Importing)), current: 0), TimeSpan.Zero), "refused");
            run.True(state.StatusLine(TimeSpan.FromSeconds(1)) is null, "and there is no line to paint");
        });

        run.Case("the clock counts the step in flight, restarts for the next, and keeps counting with no new view", () =>
        {
            ImportWindowState state = Begun();
            (ImportStepKind, ImportStepState)[] first = [(ImportStepKind.LinkSiteIfc, ImportStepState.Importing), (ImportStepKind.Vegetation, ImportStepState.Waiting)];
            state.ShowRun(View(Rows(first), current: 0), TimeSpan.FromSeconds(10));

            run.Equal(WindowLabels.Elapsed(state.Elapsed(TimeSpan.FromSeconds(10))), "0 s", "the step starts at nothing");
            run.Equal(state.StatusLine(TimeSpan.FromSeconds(55)), "Site Model: 45 s", "and counts on with no view posted");

            state.Refresh(View(Rows(first), current: 0), TimeSpan.FromSeconds(60));
            run.Equal(state.StatusLine(TimeSpan.FromSeconds(70)), "Site Model: 1 min 0 s", "a view of the same step keeps its clock");

            state.Refresh(
                View(Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Done), (ImportStepKind.Vegetation, ImportStepState.Importing)), current: 1),
                TimeSpan.FromSeconds(80));
            run.Equal(state.StatusLine(TimeSpan.FromSeconds(83)), "Planting: 3 s", "the next step starts its own");
        });

        run.Case("Cancel between steps is said at once, asked once, and the run stops at its boundary", () =>
        {
            ImportWindowState state = Begun();
            (ImportStepKind, ImportStepState)[] rows = [(ImportStepKind.LinkSiteIfc, ImportStepState.Importing), (ImportStepKind.Vegetation, ImportStepState.Waiting)];
            state.ShowRun(View(rows, current: 0), TimeSpan.Zero);

            run.Equal(state.PressCancel(), ImportWindowCancel.AskCancel, "the first press asks the run to stop");
            run.False(state.CancelEnabled, "Cancel goes dark");
            run.Equal(state.PressCancel(), ImportWindowCancel.Nothing, "a second press asks nothing more");
            run.Equal(
                state.StatusLine(TimeSpan.FromSeconds(4)),
                "Site Model: 4 s. Cancelling at the next step or chunk.",
                "said from the moment it is pressed, before the run has heard it");

            ImportRunView stopped = View(
                Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Done), (ImportStepKind.Vegetation, ImportStepState.Cancelled)),
                current: -1);
            run.Equal(state.Finish(stopped, TimeSpan.FromSeconds(5)), ImportWindowFinish.Shown, "the report shows");
            run.Equal(States(state), "Done|Cancelled", "the step after the boundary never ran");
            run.True(state.CloseEnabled, "and the window is the curator's to close");
            run.False(state.CancelEnabled, "with nothing left to cancel");
        });

        run.Case("a close during the run waits for the run to stop, then the window goes", () =>
        {
            ImportWindowState state = Begun();
            state.ShowRun(View(Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Importing)), current: 0), TimeSpan.Zero);

            run.Equal(state.Closing(), ImportWindowClose.StayAndCancel, "the close asks for the cancel");
            run.Equal(state.Closing(), ImportWindowClose.Stay, "a second close asks nothing more");
            run.Equal(state.Finish(null, TimeSpan.FromSeconds(2)), ImportWindowFinish.Close, "once the run has stopped, the window goes");
            run.False(state.ShowsFeedbackAsk, "and asks nothing on its way out");
            run.Equal(state.Closing(), ImportWindowClose.Go, "its own close then simply goes");
        });

        run.Case("once finished, the clock stops and later views change nothing", () =>
        {
            ImportWindowState state = Begun();
            (ImportStepKind, ImportStepState)[] rows = [(ImportStepKind.LinkSiteIfc, ImportStepState.Importing)];
            state.ShowRun(View(rows, current: 0), TimeSpan.Zero);

            run.Equal(
                state.Finish(View(Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Done)), current: -1), TimeSpan.FromSeconds(12)),
                ImportWindowFinish.Shown,
                "finished");
            run.True(state.ShowsFeedbackAsk, "a window the curator is still looking at may ask");
            run.Equal(
                WindowLabels.Elapsed(state.Elapsed(TimeSpan.FromSeconds(500))),
                WindowLabels.Elapsed(state.Elapsed(TimeSpan.FromSeconds(13))),
                "the clock stopped at the end");
            run.True(state.StatusLine(TimeSpan.FromSeconds(500)) is null, "the status line is the outcome's now, not the clock's");
            run.False(state.Refresh(View(rows, current: 0), TimeSpan.FromSeconds(501)), "a late view is not taken");
            run.Equal(state.Finish(null, TimeSpan.FromSeconds(502)), ImportWindowFinish.Ignored, "nor a second finish");
        });

        run.Case("a window closed outright takes nothing more", () =>
        {
            ImportWindowState state = Begun();
            state.CloseNow();
            run.Equal(state.Closing(), ImportWindowClose.Go, "its close goes without a cancel");
            run.False(state.ShowRun(View(Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Importing)), current: 0), TimeSpan.Zero), "and a run arriving after is not shown");
        });

        run.Case("the status line names the host that is committing", () =>
        {
            ImportWindowState revit = Begun("Revit");
            ImportWindowState autocad = Begun("AutoCAD");
            ImportRunView committing = View(Rows((ImportStepKind.ImageryDrape, ImportStepState.Importing)), current: 0, committing: true);
            revit.ShowRun(committing, TimeSpan.Zero);
            autocad.ShowRun(committing, TimeSpan.Zero);

            run.Equal(revit.StatusLine(TimeSpan.FromSeconds(80)), "Imagery Drape: Revit is committing, 1 min 20 s", "Revit's words, as they always were");
            run.Equal(autocad.StatusLine(TimeSpan.FromSeconds(80)), "Imagery Drape: AutoCAD is committing, 1 min 20 s", "AutoCAD's own");
        });

        run.Case("a host that supplies a step's measured cost gets an estimate beside the clock; one that does not, none", () =>
        {
            Dictionary<ImportStepKind, TimeSpan> measured = new() { [ImportStepKind.LinkSiteIfc] = TimeSpan.FromSeconds(150) };
            ImportWindowState state = Begun("AutoCAD", measured);
            (ImportStepKind, ImportStepState)[] rows = [(ImportStepKind.LinkSiteIfc, ImportStepState.Importing), (ImportStepKind.Vegetation, ImportStepState.Waiting)];
            state.ShowRun(View(rows, current: 0), TimeSpan.Zero);

            run.Equal(state.StatusLine(TimeSpan.FromSeconds(40)), "Site Model: 40 s of about 2 min 30 s", "the measured step");

            state.Refresh(
                View(Rows((ImportStepKind.LinkSiteIfc, ImportStepState.Done), (ImportStepKind.Vegetation, ImportStepState.Importing)), current: 1),
                TimeSpan.FromSeconds(50));
            run.Equal(state.StatusLine(TimeSpan.FromSeconds(55)), "Planting: 5 s", "a step with no measured cost says only its clock");
        });

        run.Case("a host whose steps are its own names its rows, and its status line says that name with no estimate", () =>
        {
            // The kind a named row carries is not read for its words or its cost: AutoCAD's steps are
            // not Revit's, and a measured cost keyed on a Revit kind must not be said beside them.
            Dictionary<ImportStepKind, TimeSpan> measured = new() { [default] = TimeSpan.FromSeconds(150) };
            ImportWindowState state = new(offersChecklist: false, "AutoCAD", measured);
            ImportRunView view = ImportRunView.From(
                [ImportRunRow.Named("Verify Files", ImportStepState.Done), ImportRunRow.Named("Contours", ImportStepState.Importing), ImportRunRow.Named("Site Linework", ImportStepState.Waiting)],
                currentIndex: 1,
                committing: true);
            state.ShowRun(view, TimeSpan.Zero);

            run.Equal(Names(state), "Verify Files|Contours|Site Linework", "each row reads the host's own name");
            run.Equal(States(state), "Done|Importing|Waiting", "in the shared state words");
            run.Equal(view.CurrentName, "Contours", "the step in flight, by its name");
            run.Equal(state.StatusLine(TimeSpan.FromSeconds(12)), "Contours: AutoCAD is committing, 12 s", "the clock alone, no estimate");
        });

        run.Case("a row with no name of its own reads the shared step name", () =>
        {
            ImportRunRow row = new(ImportStepKind.PublishedContours, ImportStepState.Waiting);
            run.Equal(row.StepName, WindowLabels.StepName(ImportStepKind.PublishedContours), "Revit's rows are as they were");
            run.Equal(ImportRunRow.Named("Aerial Image", ImportStepState.Waiting).StepName, "Aerial Image", "a named row reads its name");
        });

        return run.Report("import window state");
    }

    private static ImportWindowState Begun(string hostName = "Revit", IReadOnlyDictionary<ImportStepKind, TimeSpan>? measured = null)
    {
        ImportWindowState state = new(offersChecklist: true, hostName, measured);
        state.PressImport(checklistAllowsImport: true);
        return state;
    }

    private static (ImportStepKind, ImportStepState)[] Rows(params (ImportStepKind, ImportStepState)[] rows) => rows;

    private static ImportRunView View((ImportStepKind Kind, ImportStepState State)[] rows, int current, bool committing = false)
        => ImportRunView.From([.. rows.Select(row => new ImportRunRow(row.Kind, row.State))], current, committing);

    private static string Names(ImportWindowState state) => string.Join("|", state.Rows.Select(row => row.Step));

    private static string States(ImportWindowState state) => string.Join("|", state.Rows.Select(row => row.State));
}
