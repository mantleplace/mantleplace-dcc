namespace MantlePlace.Core;

public sealed class LevelSwitch
{
    public LevelSwitch(LevelControl control)
    {
        Control = control ?? throw new ArgumentNullException(nameof(control));
        Held = control.Selected;
    }

    public LevelControl Control { get; }

    public FidelityLevel Held { get; private set; }

    public bool Fetching { get; private set; }

    public bool CanImport => !Fetching && Control.Selected == Held;

    public FidelityLevel? Choose(FidelityLevel level)
    {
        if (Fetching || !Control.Choose(level) || level == Held)
        {
            return null;
        }

        Fetching = true;
        return level;
    }

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
