namespace MantlePlace.Revit.Core;

public sealed partial class ImportChecklist
{
    /// <summary>
    /// Opens the checklist on what the curator ticked the last time they pressed Import. A <c>null</c>
    /// memory changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a layer this bundle carries is touched, so a remembered choice never turns on a layer the
    /// bundle lacks; and only a layer the memory covers, so any other starts at its default.
    /// </para>
    /// <para>
    /// The memory is written to what the curator wants, not to what shows, so
    /// <see cref="IsChecked"/> and <see cref="IsEnabled"/> still govern: a box whose prerequisite is
    /// off opens unchecked and disabled, and comes back as remembered when the prerequisite is ticked,
    /// exactly as a box the curator had ticked in this window would. A level goes through
    /// <see cref="SetLevel"/>, so one this bundle cannot import is ignored and the row keeps its default.
    /// </para>
    /// </remarks>
    public void Restore(RememberedChoice? remembered)
    {
        if (remembered is null)
        {
            return;
        }

        foreach (ImportLayer layer in Layers)
        {
            if (remembered.Ticked(layer) is { } ticked)
            {
                if (ticked)
                {
                    _wanted.Add(layer);
                }
                else
                {
                    _wanted.Remove(layer);
                }
            }

            if (remembered.Level(layer) is { } level)
            {
                SetLevel(layer, level);
            }
        }
    }
}
