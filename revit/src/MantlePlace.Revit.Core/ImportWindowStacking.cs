namespace MantlePlace.Revit.Core;

/// <summary>Whose window is in front, as far as the import window needs to know.</summary>
public enum ForegroundHolder
{
    /// <summary>No window is in front, or one nobody can trace: Windows is between two.</summary>
    Nobody,

    /// <summary>The import window itself.</summary>
    ImportWindow,

    /// <summary>Revit's main window, with no modal up.</summary>
    RevitMainWindow,

    /// <summary>Another of Revit's windows — the vault window, a palette, a dialog — with no modal up.</summary>
    RevitOtherWindow,

    /// <summary>Any of Revit's windows while Revit shows a modal, the import window included.</summary>
    RevitModal,

    /// <summary>The stand-in Windows draws over this Revit while it is stuck, in a commit or otherwise.</summary>
    RevitGhost,

    /// <summary>The shell's desktop, which is no application to go behind.</summary>
    Desktop,

    /// <summary>Another application's window, or its ghost.</summary>
    OtherProcess,
}

/// <summary>
/// What the shim reads about the window in front, without sending it a message.
/// </summary>
/// <param name="Exists">Whether any window is in front.</param>
/// <param name="IsImportWindow">Whether it is the import window itself.</param>
/// <param name="InThisProcess">Whether it is one of Revit's process's windows.</param>
/// <param name="IsRevitMainWindow">Whether it is Revit's main window.</param>
/// <param name="RevitMainWindowEnabled">
/// Whether Revit's main window takes input. A modal disables the window it belongs to, so a disabled
/// main window is how a Revit modal is told (<see cref="ImportWindowStacking.Classify"/>).
/// </param>
/// <param name="ForegroundFloats">Whether the window in front floats over others itself.</param>
/// <param name="ClassName">Its window class.</param>
/// <param name="HungWindowOfThisProcess">
/// For a ghost: whether the window it stands in for is one of Revit's. <c>null</c> when that could not
/// be looked up.
/// </param>
public readonly record struct ForegroundFacts(
    bool Exists,
    bool IsImportWindow,
    bool InThisProcess,
    bool IsRevitMainWindow,
    bool RevitMainWindowEnabled,
    bool ForegroundFloats,
    string ClassName,
    bool? HungWindowOfThisProcess);

/// <summary>What to do to the import window's place in the z-order.</summary>
public enum StackingMove
{
    /// <summary>Leave it where it is.</summary>
    None,

    /// <summary>Float it over every ordinary window.</summary>
    Float,

    /// <summary>Put it directly behind the window in front.</summary>
    SinkBehindForeground,

    /// <summary>Put it on top of the ordinary windows, below every floating one.</summary>
    SinkBelowFloating,
}

/// <summary>Whether the import window floats, and what to do to get it there.</summary>
public readonly record struct Stacking(bool Floating, StackingMove Move);

/// <summary>
/// How the import window keeps in front of Revit without Revit's window owning it, and when it may
/// take the focus. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Why it is not owned is <c>revit/CLAUDE.md</c>'s, and what that gives up is <c>revit/README.md</c>'s.
/// The rules here replace ownership.
/// </para>
/// <para>
/// <b>It floats while Revit is in front</b> — its main window, the import window itself, or the ghost
/// Windows draws over a Revit stuck in a commit, which is what the curator clicks when looking for the
/// window. <b>It goes behind anything else that comes forward</b>: another application, or another of
/// Revit's own windows.
/// </para>
/// <para>
/// ⛔ <b>It never floats over a Revit modal.</b> The window is centred over Revit, which is where
/// Revit centres its dialogs, so floating while one is up would hide a dialog Revit is blocked on — the
/// "Another import is open." dialog, a fault's dialog, a save prompt. A modal disables the window it
/// belongs to, so while Revit's main window is disabled nothing of Revit's counts as Revit in front,
/// and the window neither floats, nor opens activated, nor is brought forward.
/// </para>
/// <para>
/// The desktop, and a moment with nothing in front, change nothing: behind the desktop is behind every
/// window there is.
/// </para>
/// </remarks>
public static class ImportWindowStacking
{
    /// <summary>The window classes of the shell's desktop.</summary>
    private static readonly string[] DesktopClasses = ["Progman", "WorkerW"];

    /// <summary>The window class Windows gives the ghost of a window that has stopped answering.</summary>
    private const string GhostClass = "Ghost";

    /// <summary>Whose window <paramref name="facts"/> describes.</summary>
    public static ForegroundHolder Classify(ForegroundFacts facts)
    {
        if (!facts.Exists)
        {
            return ForegroundHolder.Nobody;
        }

        bool ghost = string.Equals(facts.ClassName, GhostClass, StringComparison.Ordinal);
        if (ghost && facts.HungWindowOfThisProcess is null)
        {
            return ForegroundHolder.Nobody;
        }

        bool revits = facts.InThisProcess || (ghost && facts.HungWindowOfThisProcess == true);
        if (revits && !facts.RevitMainWindowEnabled)
        {
            return ForegroundHolder.RevitModal;
        }

        if (ghost)
        {
            return revits ? ForegroundHolder.RevitGhost : ForegroundHolder.OtherProcess;
        }

        if (facts.IsImportWindow)
        {
            return ForegroundHolder.ImportWindow;
        }

        if (facts.InThisProcess)
        {
            return facts.IsRevitMainWindow ? ForegroundHolder.RevitMainWindow : ForegroundHolder.RevitOtherWindow;
        }

        return Array.IndexOf(DesktopClasses, facts.ClassName) >= 0 ? ForegroundHolder.Desktop : ForegroundHolder.OtherProcess;
    }

    /// <summary>
    /// Where a window just created goes, before it is shown. It starts on top of the ordinary
    /// windows, so one that should not float is put behind what is in front, or it would flash over it.
    /// </summary>
    /// <remarks>
    /// One that opens with the focus (<see cref="ShowsActivated"/>) floats from the start: it is in
    /// front the moment it is shown, and the import window in front is a window that floats.
    /// </remarks>
    public static Stacking First(ForegroundFacts facts)
    {
        if (ShowsActivated(facts))
        {
            return new Stacking(true, StackingMove.Float);
        }

        return Floats(Classify(facts)) switch
        {
            true => new Stacking(true, StackingMove.Float),
            false => new Stacking(false, Sink(facts)),
            null => new Stacking(false, StackingMove.None),
        };
    }

    /// <summary>Where the window goes when <paramref name="facts"/>' window comes forward.</summary>
    /// <param name="facts">The window that has just come to the front.</param>
    /// <param name="floating">Whether the import window floats now.</param>
    public static Stacking Next(ForegroundFacts facts, bool floating) => Floats(Classify(facts)) switch
    {
        true => new Stacking(true, floating ? StackingMove.None : StackingMove.Float),

        // Not floating, it is already behind what came forward: activating a window brings it to the
        // top of the ordinary windows.
        false => new Stacking(false, floating ? Sink(facts) : StackingMove.None),
        null => new Stacking(floating, StackingMove.None),
    };

    /// <summary>
    /// Where the window goes when Revit is about to show a dialog: among the ordinary windows, where
    /// the dialog opens on top of it, rather than above them. The next change of foreground decides
    /// again.
    /// </summary>
    public static Stacking Lowered(bool floating)
        => floating ? new Stacking(false, StackingMove.SinkBelowFloating) : new Stacking(false, StackingMove.None);

    /// <summary>Whether the window takes the focus as it opens.</summary>
    /// <remarks>
    /// Only when Revit was in front with no modal up: the ribbon's Import Bundle, or the vault's
    /// Import. A window opened while the curator is in another application, or by a harness, does not
    /// take the keyboard from it.
    /// </remarks>
    public static bool ShowsActivated(ForegroundFacts facts)
        => Classify(facts) is ForegroundHolder.RevitMainWindow or ForegroundHolder.RevitOtherWindow;

    /// <summary>Whether a second Import Bundle, or the vault's Import, brings the running import's window forward now.</summary>
    public static bool BringsForward(ForegroundFacts facts) => Classify(facts) != ForegroundHolder.RevitModal;

    /// <summary><c>true</c> to float, <c>false</c> to go behind, <c>null</c> to change nothing.</summary>
    private static bool? Floats(ForegroundHolder holder) => holder switch
    {
        ForegroundHolder.ImportWindow or ForegroundHolder.RevitMainWindow or ForegroundHolder.RevitGhost => true,
        ForegroundHolder.RevitOtherWindow or ForegroundHolder.RevitModal or ForegroundHolder.OtherProcess => false,
        _ => null,
    };

    /// <summary>
    /// How to go behind what is in front: directly behind it, unless it floats itself or is the import
    /// window, when the most there is to do is to stop floating.
    /// </summary>
    private static StackingMove Sink(ForegroundFacts facts)
        => facts.Exists && !facts.IsImportWindow && !facts.ForegroundFloats
            ? StackingMove.SinkBehindForeground
            : StackingMove.SinkBelowFloating;
}

/// <summary>Where the import window opens. Pure.</summary>
public static class ImportWindowPlacement
{
    /// <summary>
    /// Centred over Revit's window, as an owned window's centre-on-owner placed it, and never hanging
    /// off Revit's left or top edge when Revit's window is the smaller.
    /// </summary>
    /// <param name="revit">Revit's window, in device-independent pixels.</param>
    /// <param name="width">The import window's width.</param>
    /// <param name="height">The import window's height, or the least it will be.</param>
    public static ScreenRect CentreOver(ScreenRect revit, double width, double height)
        => new(
            revit.Left + Math.Max(0, (revit.Width - width) / 2),
            revit.Top + Math.Max(0, (revit.Height - height) / 2),
            width,
            height);

    /// <summary>
    /// Where a minimised window will be when it is restored, on the screen, in device pixels.
    /// </summary>
    /// <param name="normalInWorkspace">
    /// Its restored rectangle as Windows keeps it, in workspace coordinates: screen coordinates less the
    /// inset of its monitor's work area from that monitor — a taskbar on the left or the top of that
    /// monitor, and nothing else.
    /// </param>
    /// <param name="workArea">That monitor's work area — the monitor less its taskbar — on the screen.</param>
    /// <param name="monitor">That monitor, on the screen.</param>
    /// <param name="restoresMaximised">Whether it restores maximised, filling that work area.</param>
    /// <remarks>
    /// Not offset by the work area's whole corner: on a monitor above the first, at (641, -2160), a
    /// window restored at that corner keeps (641, -2160) as its restored place, and adding the corner
    /// again would put it at (1282, -4320), off every screen.
    /// </remarks>
    public static ScreenRect Restored(ScreenRect normalInWorkspace, ScreenRect workArea, ScreenRect monitor, bool restoresMaximised)
        => restoresMaximised
            ? workArea
            : normalInWorkspace with
            {
                Left = normalInWorkspace.Left + (workArea.Left - monitor.Left),
                Top = normalInWorkspace.Top + (workArea.Top - monitor.Top),
            };
}
