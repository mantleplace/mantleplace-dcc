namespace MantlePlace.Revit.Core;

/// <summary>Whose window is in front, as far as the import window needs to know.</summary>
public enum ForegroundHolder
{
    /// <summary>No window is in front: Windows is between two.</summary>
    Nobody,

    /// <summary>A window of Revit's process — Revit's own, or the import window itself.</summary>
    ThisProcess,

    /// <summary>The stand-in Windows draws in place of a window that has stopped answering.</summary>
    HungWindowGhost,

    /// <summary>Another application's window.</summary>
    OtherProcess,
}

/// <summary>
/// How the import window keeps in front of Revit without Revit's window owning it, and when it may
/// take the focus. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>It must not be owned.</b> It used to be owned by Revit's main window, which is what kept it
/// in front of the model it was changing — and what froze it: a window and its owner share one input
/// queue, so when Revit's thread sat in a commit for twenty minutes the window sat with it, however
/// many threads it had. So it is a top-level window on a thread of its own, and staying in front of
/// Revit is done here instead.
/// </para>
/// <para>
/// <b>It floats while Revit is the application in front, and not otherwise.</b> Floating always would
/// hold it over the curator's browser for a whole step; never floating would lose it behind Revit at
/// the first click on the model. The one case that needs a thought is a Revit stuck in a commit:
/// Windows draws a ghost in place of a window that has stopped answering, and the ghost belongs to
/// another process, so a click on the frozen Revit reads as "another application came forward". Here
/// that changes nothing.
/// </para>
/// <para>
/// What is given up, against an owned window: it no longer minimises with Revit, and it has a
/// taskbar button of its own, which is how it is found again when it is behind something.
/// </para>
/// </remarks>
public static class ImportWindowStacking
{
    /// <summary>Whether the window should float over other windows now.</summary>
    /// <param name="holder">Whose window has just come to the front.</param>
    /// <param name="floating">Whether it floats now.</param>
    public static bool Floats(ForegroundHolder holder, bool floating) => holder switch
    {
        ForegroundHolder.ThisProcess => true,
        ForegroundHolder.OtherProcess => false,
        _ => floating,
    };

    /// <summary>Whether the window takes the focus as it opens.</summary>
    /// <remarks>
    /// Only when Revit is in front, which is the ribbon's Import Bundle and the vault's Import. A
    /// window opened while the curator is in another application — or one a harness opens — does not
    /// take the keyboard from it.
    /// </remarks>
    public static bool ShowsActivated(ForegroundHolder holder) => holder == ForegroundHolder.ThisProcess;
}
