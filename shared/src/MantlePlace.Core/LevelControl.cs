namespace MantlePlace.Core;

public readonly record struct DownloadLevelOption(FidelityLevel Level, string Label, string ToolTip, bool IsRecommended);

public sealed class LevelControl
{
    private LevelControl(VaultBundle bundle, LevelOffer offer)
    {
        Bundle = bundle;
        Offer = offer;
        Options = [.. offer.Levels.Select(size => new DownloadLevelOption(
            size.Level,
            WindowLabels.LevelChoice(size.Level, size.SizeBytes, size.Level == offer.Recommended),
            WindowLabels.LevelCounts(size),
            size.Level == offer.Recommended))];
        Selected = offer.Recommended;
    }

    public VaultBundle Bundle { get; }

    public LevelOffer Offer { get; }

    public IReadOnlyList<DownloadLevelOption> Options { get; }

    public FidelityLevel Selected { get; private set; }

    public static string Heading => WindowLabels.DownloadLevelHeading;

    public int SelectedIndex => Options.ToList().FindIndex(option => option.Level == Selected);

    public string SlotLevel => DownloadLevels.SlotLevel(Bundle, Selected);

    public static LevelControl? For(VaultBundle? bundle)
        => bundle?.Levels is { } offer ? new LevelControl(bundle, offer) : null;

    public static LevelControl? At(VaultBundle? bundle, FidelityLevel level)
    {
        LevelControl? control = For(bundle);
        _ = control?.Choose(level);
        return control;
    }

    public bool Choose(FidelityLevel level)
    {
        if (!Options.Any(option => option.Level == level))
        {
            return false;
        }

        Selected = level;
        return true;
    }
}
