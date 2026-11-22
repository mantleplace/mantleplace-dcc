namespace MantlePlace.Core;

public sealed partial class ImportChecklist
{
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
