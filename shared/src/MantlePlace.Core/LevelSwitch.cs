namespace MantlePlace.Core;

/// <summary>
/// The level control in the window a received send opens: the bundle is already on disk at one level,
/// and a different choice must be downloaded into the slot before Import. Pure; owned by the window's thread.
/// </summary>
/// <remarks>
/// <para>
/// A send names no level, so its bundle is fetched at the platform's recommendation and the window
/// opens with the control on it (<see cref="Held"/>). The curator may choose another level: the choice
/// asks for a fresh download (<see cref="Choose"/> answers the level to fetch), Import stays off while it
/// runs, and the window goes on at the new level once it is in the slot (<see cref="Fetched"/>). A
/// download that fails puts the control back on the level the slot still holds.
/// </para>
/// <para>
/// The same state in both hosts: AutoCAD's received-export window runs it in place, and Revit's import
/// window, whose checklist is read from the files, reopens on the fresh download at the chosen level.
/// </para>
/// </remarks>
public sealed class LevelSwitch
{
    public LevelSwitch(LevelControl control)
    {
        Control = control ?? throw new ArgumentNullException(nameof(control));
        Held = control.Selected;
    }

    /// <summary>The control the window shows.</summary>
    public LevelControl Control { get; }

    /// <summary>The level the bundle on disk is at: the one Import would read.</summary>
    public FidelityLevel Held { get; private set; }

    /// <summary>Whether a download at the chosen level is running.</summary>
    public bool Fetching { get; private set; }

    /// <summary>Whether Import may run: nothing is downloading, and the slot holds the chosen level.</summary>
    public bool CanImport => !Fetching && Control.Selected == Held;

    /// <summary>
    /// The curator chooses <paramref name="level"/>. The level to download, or <c>null</c> when there is
    /// nothing to fetch: the slot already holds it, a download is already running, or it is not offered.
    /// </summary>
    public FidelityLevel? Choose(FidelityLevel level)
    {
        if (Fetching || !Control.Choose(level) || level == Held)
        {
            return null;
        }

        Fetching = true;
        return level;
    }

    /// <summary>
    /// The download ended. On success the slot holds the chosen level; on failure the control goes back
    /// to the level the slot still holds.
    /// </summary>
    public void Fetched(bool ready)
    {
        Fetching = false;
        if (ready)
        {
            Held = Control.Selected;
            return;
        }

        _ = Control.Choose(Held);
    }
}
