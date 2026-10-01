using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// How the import window keeps in front of Revit without Revit's window owning it, where it opens,
/// and when it may take the focus.
/// </summary>
/// <remarks>
/// Why it is not owned is in <c>revit/CLAUDE.md</c>, and what that gives up is in
/// <c>revit/README.md</c>. These are the rules that replace ownership, read without a desktop.
/// </remarks>
internal static class ImportWindowStackingTests
{
    private static readonly ForegroundFacts Nothing = new(
        Exists: false,
        IsImportWindow: false,
        InThisProcess: false,
        IsRevitMainWindow: false,
        RevitMainWindowEnabled: true,
        ForegroundFloats: false,
        ClassName: string.Empty,
        HungWindowOfThisProcess: false);

    private static readonly ForegroundFacts RevitMain = Nothing with { Exists = true, InThisProcess = true, IsRevitMainWindow = true, ClassName = "AfxFrameOrView140u" };
    private static readonly ForegroundFacts ImportWindow = Nothing with { Exists = true, InThisProcess = true, IsImportWindow = true, ClassName = "HwndWrapper" };
    private static readonly ForegroundFacts RevitDialog = Nothing with { Exists = true, InThisProcess = true, ClassName = "a dialog" };
    private static readonly ForegroundFacts OtherApp = Nothing with { Exists = true, ClassName = "Chrome_WidgetWin_1" };
    private static readonly ForegroundFacts FloatingOtherApp = OtherApp with { ForegroundFloats = true };
    private static readonly ForegroundFacts RevitGhost = Nothing with { Exists = true, ClassName = "Ghost", HungWindowOfThisProcess = true };
    private static readonly ForegroundFacts OtherGhost = Nothing with { Exists = true, ClassName = "Ghost", HungWindowOfThisProcess = false };
    private static readonly ForegroundFacts UnknownGhost = Nothing with { Exists = true, ClassName = "Ghost", HungWindowOfThisProcess = null };
    private static readonly ForegroundFacts Desktop = Nothing with { Exists = true, ClassName = "Progman" };
    private static readonly ForegroundFacts DesktopWorker = Nothing with { Exists = true, ClassName = "WorkerW" };

    internal static int Run()
    {
        TestRun run = new();

        run.Case("whose window is in front, as the import window reads it", () =>
        {
            run.Equal(ImportWindowStacking.Classify(Nothing), ForegroundHolder.Nobody, "nothing in front");
            run.Equal(ImportWindowStacking.Classify(ImportWindow), ForegroundHolder.ImportWindow, "the import window itself");
            run.Equal(ImportWindowStacking.Classify(RevitMain), ForegroundHolder.RevitMainWindow, "Revit's main window");
            run.Equal(ImportWindowStacking.Classify(RevitDialog), ForegroundHolder.RevitOtherWindow, "another of Revit's windows, the vault's or a dialog");
            run.Equal(ImportWindowStacking.Classify(OtherApp), ForegroundHolder.OtherProcess, "another application");
            run.Equal(ImportWindowStacking.Classify(RevitGhost), ForegroundHolder.RevitGhost, "the ghost Windows draws over this Revit, stuck in a commit");
            run.Equal(ImportWindowStacking.Classify(OtherGhost), ForegroundHolder.OtherProcess, "another application's ghost is that application");
            run.Equal(ImportWindowStacking.Classify(UnknownGhost), ForegroundHolder.Nobody, "a ghost nobody can trace changes nothing");
            run.Equal(ImportWindowStacking.Classify(Desktop), ForegroundHolder.Desktop, "the shell's desktop");
            run.Equal(ImportWindowStacking.Classify(DesktopWorker), ForegroundHolder.Desktop, "the desktop's worker window");
        });

        run.Case("while Revit shows a modal, nothing of Revit's counts as Revit in front", () =>
        {
            // A modal disables the window it belongs to, so Revit's main window reads disabled for as
            // long as one is up — the "Another import is open." dialog, a fault's dialog, a save
            // prompt — whichever of Revit's windows is in front then, the import window included.
            foreach (ForegroundFacts facts in new[] { RevitMain, ImportWindow, RevitDialog, RevitGhost })
            {
                run.Equal(
                    ImportWindowStacking.Classify(facts with { RevitMainWindowEnabled = false }),
                    ForegroundHolder.RevitModal,
                    $"{facts.ClassName} while a modal is up");
            }
        });

        run.Case("it floats while Revit is in front, and over this Revit's ghost", () =>
        {
            // The ghost is what the curator clicks when Revit is stuck in a commit: exactly when they
            // are looking for the window, so it comes forward rather than staying buried.
            foreach (ForegroundFacts facts in new[] { RevitMain, ImportWindow, RevitGhost })
            {
                run.True(ImportWindowStacking.Next(facts, floating: false) == new Stacking(true, StackingMove.Float), $"{facts.ClassName} from behind");
                run.True(ImportWindowStacking.Next(facts, floating: true) == new Stacking(true, StackingMove.None), $"{facts.ClassName} while floating");
            }
        });

        run.Case("it never floats over a Revit modal or another of Revit's windows", () =>
        {
            // The window is centred over Revit, which is where Revit centres its dialogs: floating
            // there would hide a dialog Revit is blocked on.
            run.True(
                ImportWindowStacking.Next(RevitDialog with { RevitMainWindowEnabled = false }, floating: true) == new Stacking(false, StackingMove.SinkBehindForeground),
                "under the modal that came forward");
            run.True(
                ImportWindowStacking.Next(RevitDialog, floating: true) == new Stacking(false, StackingMove.SinkBehindForeground),
                "under the vault, or a Revit dialog, the curator turned to");
            run.True(
                ImportWindowStacking.Next(ImportWindow with { RevitMainWindowEnabled = false }, floating: true) == new Stacking(false, StackingMove.SinkBelowFloating),
                "in front itself while a modal is up: it stops floating, and cannot go behind itself");
            run.True(
                ImportWindowStacking.Next(RevitMain with { RevitMainWindowEnabled = false }, floating: false) == new Stacking(false, StackingMove.None),
                "a modal is up and it is not floating: nothing to undo");
        });

        run.Case("it stops floating when the curator turns to another application, and goes behind it", () =>
        {
            // Behind that application, not on top of the ordinary windows, which would put it over
            // their browser for the whole of a long step.
            run.True(ImportWindowStacking.Next(OtherApp, floating: true) == new Stacking(false, StackingMove.SinkBehindForeground), "behind it");
            run.True(ImportWindowStacking.Next(OtherGhost, floating: true) == new Stacking(false, StackingMove.SinkBehindForeground), "behind another application's ghost");
            run.True(ImportWindowStacking.Next(FloatingOtherApp, floating: true) == new Stacking(false, StackingMove.SinkBelowFloating), "an application that floats itself: below the floating ones");
            run.True(ImportWindowStacking.Next(OtherApp, floating: false) == new Stacking(false, StackingMove.None), "already behind it");
        });

        run.Case("the desktop, and a moment with nothing in front, change nothing", () =>
        {
            // Going behind the desktop would put the window under every other window there is.
            foreach (ForegroundFacts facts in new[] { Desktop, DesktopWorker, Nothing, UnknownGhost })
            {
                run.True(ImportWindowStacking.Next(facts, floating: true) == new Stacking(true, StackingMove.None), $"'{facts.ClassName}' while floating");
                run.True(ImportWindowStacking.Next(facts, floating: false) == new Stacking(false, StackingMove.None), $"'{facts.ClassName}' while not");
            }
        });

        run.Case("a new window is stacked before it is shown", () =>
        {
            // A window starts on top of the ordinary windows, so one that should not float has to be
            // put behind what is in front before it appears, or it flashes over another application.
            run.True(ImportWindowStacking.First(RevitMain) == new Stacking(true, StackingMove.Float), "Revit in front: it floats");
            run.True(
                ImportWindowStacking.First(RevitDialog) == new Stacking(true, StackingMove.Float),
                "opened from the vault it takes the focus, so it floats from the start, as it would the moment it had the focus");
            run.True(ImportWindowStacking.First(OtherApp) == new Stacking(false, StackingMove.SinkBehindForeground), "another application: behind it");
            run.True(ImportWindowStacking.First(FloatingOtherApp) == new Stacking(false, StackingMove.SinkBelowFloating), "one that floats: below the floating ones");
            run.True(ImportWindowStacking.First(RevitDialog with { RevitMainWindowEnabled = false }) == new Stacking(false, StackingMove.SinkBehindForeground), "a Revit modal: behind it");
            run.True(ImportWindowStacking.First(Desktop) == new Stacking(false, StackingMove.None), "the desktop: where it is");
            run.True(ImportWindowStacking.First(Nothing) == new Stacking(false, StackingMove.None), "nothing: where it is");
        });

        run.Case("it takes the focus only from Revit, and never from a modal", () =>
        {
            // Pressing Import Bundle, or the vault's Import, expects the checklist to take the
            // keyboard. An import opened while the curator is elsewhere, or by a harness, does not.
            run.True(ImportWindowStacking.ShowsActivated(RevitMain), "from Revit's main window");
            run.True(ImportWindowStacking.ShowsActivated(RevitDialog), "from the vault window");
            run.False(ImportWindowStacking.ShowsActivated(RevitDialog with { RevitMainWindowEnabled = false }), "not over a modal");
            run.False(ImportWindowStacking.ShowsActivated(OtherApp), "not from another application");
            run.False(ImportWindowStacking.ShowsActivated(RevitGhost), "not from a hung Revit");
            run.False(ImportWindowStacking.ShowsActivated(Desktop), "not from the desktop");
            run.False(ImportWindowStacking.ShowsActivated(Nothing), "not from nothing");
        });

        run.Case("lowered for a Revit dialog, it stops floating and sits below the floating windows", () =>
        {
            // Revit is about to show a dialog, which opens on top of the ordinary windows: the import
            // window has to be among them, not above them, before it does.
            run.True(ImportWindowStacking.Lowered(floating: true) == new Stacking(false, StackingMove.SinkBelowFloating), "floating: lowered");
            run.True(ImportWindowStacking.Lowered(floating: false) == new Stacking(false, StackingMove.None), "not floating: nothing to do");
        });

        run.Case("a second Import Bundle brings it forward, but never over a modal", () =>
        {
            run.True(ImportWindowStacking.BringsForward(RevitMain), "Revit in front");
            run.False(ImportWindowStacking.BringsForward(RevitMain with { RevitMainWindowEnabled = false }), "the busy dialog, or any modal, is up");
        });

        run.Case("it opens centred over Revit, and never hangs off Revit's left or top edge", () =>
        {
            ScreenRect revit = new(100, 50, 1600, 900);
            run.True(ImportWindowPlacement.CentreOver(revit, 520, 548) == new ScreenRect(640, 226, 520, 548), "centred");

            ScreenRect small = new(-1900, 200, 400, 300);
            run.True(ImportWindowPlacement.CentreOver(small, 520, 548) == new ScreenRect(-1900, 200, 520, 548), "a Revit smaller than the window: its top-left corner");
        });

        run.Case("a minimised Revit is placed where it will restore to, on the screen", () =>
        {
            // Windows keeps a minimised window's restored place in workspace coordinates: from the top
            // left of its monitor's work area, which a taskbar on the left or the top moves.
            ScreenRect normal = new(100, 50, 800, 600);
            ScreenRect workArea = new(60, 0, 1860, 1080);
            run.True(
                ImportWindowPlacement.Restored(normal, workArea, restoresMaximised: false) == new ScreenRect(160, 50, 800, 600),
                "offset by the work area's corner");
            run.True(
                ImportWindowPlacement.Restored(normal, workArea, restoresMaximised: true) == workArea,
                "one that restores maximised fills the work area");

            ScreenRect secondMonitor = new(1920, -1080, 1920, 1040);
            run.True(
                ImportWindowPlacement.Restored(new ScreenRect(10, 20, 400, 300), secondMonitor, restoresMaximised: false) == new ScreenRect(1930, -1060, 400, 300),
                "on a monitor above and to the right of the first");
        });

        return run.Report("import window stacking");
    }
}
