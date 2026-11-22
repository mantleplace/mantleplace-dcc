namespace MantlePlace.Core;

public sealed class LevelChoices
{
    private readonly Dictionary<string, FidelityLevel> _chosen = new(StringComparer.Ordinal);

    public LevelControl? ControlFor(VaultBundle? bundle)
        => bundle is not null && _chosen.TryGetValue(bundle.OrderId, out FidelityLevel level)
            ? LevelControl.At(bundle, level)
            : LevelControl.For(bundle);

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
