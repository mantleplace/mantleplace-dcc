namespace MantlePlace.Core;

/// <summary>
/// The levels a curator has chosen in one vault window, by order, so a row that is refreshed keeps
/// its choice. Pure; owned by the window's thread.
/// </summary>
/// <remarks>
/// The window re-seats a row to redraw it, and a re-seat clears the list's selection and selects the
/// row again. The control the window shows is built each time the selection changes, so a control that
/// lived only as long as the selection lost the choice on every refresh and fell back to the
/// recommendation. The choice lives here instead: the recommendation is the preselection only until the
/// curator chooses. A 2.x order has no control and so no choice.
/// </remarks>
public sealed class LevelChoices
{
    private readonly Dictionary<string, FidelityLevel> _chosen = new(StringComparer.Ordinal);

    /// <summary>
    /// The control for <paramref name="bundle"/>: on the curator's choice for that order when there is
    /// one still offered, else on the recommendation. <c>null</c> for a 2.x order or no order.
    /// </summary>
    public LevelControl? ControlFor(VaultBundle? bundle)
        => bundle is not null && _chosen.TryGetValue(bundle.OrderId, out FidelityLevel level)
            ? LevelControl.At(bundle, level)
            : LevelControl.For(bundle);

    /// <summary>The curator chooses <paramref name="level"/> on <paramref name="control"/>. <c>false</c>, and nothing changes, for a level not offered.</summary>
    public bool Choose(LevelControl control, FidelityLevel level)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!control.Choose(level))
        {
            return false;
        }

        _chosen[control.Bundle.OrderId] = level;
        return true;
    }
}
