using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// How the import window stays in front of Revit now that Revit's window no longer owns it, and when
/// it may take the focus.
/// </summary>
/// <remarks>
/// An owned window floats over its owner and minimises with it, and that ownership is what froze the
/// window: an owner and the window it owns share one input queue, so a Revit stuck in a commit took
/// the window down with it. Unowned, the window has to be kept in front some other way, and the way
/// chosen is decided here, where it can be read without a desktop.
/// </remarks>
internal static class ImportWindowStackingTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("it floats while Revit is the application in front, whichever of its windows that is", () =>
        {
            // The import window is Revit's process too, so clicking it keeps it floating.
            run.True(ImportWindowStacking.Floats(ForegroundHolder.ThisProcess, floating: false), "over Revit, once Revit comes forward");
            run.True(ImportWindowStacking.Floats(ForegroundHolder.ThisProcess, floating: true), "and it stays there");
        });

        run.Case("it stops floating when the curator turns to another application", () =>
        {
            // Floating over everything would put an import window over their browser for the whole of
            // a 45-minute step.
            run.False(ImportWindowStacking.Floats(ForegroundHolder.OtherProcess, floating: true), "another application comes forward");
            run.False(ImportWindowStacking.Floats(ForegroundHolder.OtherProcess, floating: false), "and it does not start");
        });

        run.Case("a hung window's stand-in changes nothing", () =>
        {
            // Clicking a Revit that is stuck in a commit brings forward the ghost Windows draws in its
            // place, which belongs to another process. Dropping there would bury the window behind the
            // very Revit it is reporting on; rising there would put it over some other application's
            // hang. So it keeps whatever it was.
            run.True(ImportWindowStacking.Floats(ForegroundHolder.HungWindowGhost, floating: true), "floating over Revit, it stays");
            run.False(ImportWindowStacking.Floats(ForegroundHolder.HungWindowGhost, floating: false), "behind another application, it stays");
        });

        run.Case("a moment with nothing in front changes nothing", () =>
        {
            // Windows passes through no foreground window at all while it switches.
            run.True(ImportWindowStacking.Floats(ForegroundHolder.Nobody, floating: true), "floating");
            run.False(ImportWindowStacking.Floats(ForegroundHolder.Nobody, floating: false), "not floating");
        });

        run.Case("it takes the focus only from Revit", () =>
        {
            // Pressing Import Bundle in Revit expects the checklist to take the keyboard. An import
            // queued from the vault while the curator has moved on, or one a harness opens, must not
            // pull the keyboard out of whatever they are typing into.
            run.True(ImportWindowStacking.ShowsActivated(ForegroundHolder.ThisProcess), "Revit is in front");
            run.False(ImportWindowStacking.ShowsActivated(ForegroundHolder.OtherProcess), "another application is");
            run.False(ImportWindowStacking.ShowsActivated(ForegroundHolder.HungWindowGhost), "a hung window's stand-in is");
            run.False(ImportWindowStacking.ShowsActivated(ForegroundHolder.Nobody), "nothing is");
        });

        return run.Report("import window stacking");
    }
}
