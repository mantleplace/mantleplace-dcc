namespace MantlePlace.Core;

/// <summary>One choice in the level control: the level, its words, and whether the platform recommends it.</summary>
/// <param name="Level">Max, Med or Min.</param>
/// <param name="Label">What the control shows: the level, its size, and the recommendation when it is the one.</param>
/// <param name="ToolTip">The level's counts, for the curator who wants more than its size.</param>
/// <param name="IsRecommended">Whether the platform recommends it for this host and site.</param>
public readonly record struct DownloadLevelOption(FidelityLevel Level, string Label, string ToolTip, bool IsRecommended);

/// <summary>
/// The level control's state: Max, Med and Min, each with its size, the platform's recommendation
/// preselected and the curator free to choose. Pure; owned by the window's thread.
/// </summary>
/// <remarks>
/// <para>
/// The same control and words in every .NET host (the shared <c>LevelChoiceRow</c>): the shared vault
/// window shows it beside Import, so the choice is made before the bundle is fetched, and in Revit the
/// window a received send opens shows it too, on the level the send was fetched at (<see cref="LevelSwitch"/>),
/// as AutoCAD's does on its stage before Import (<c>ImportWindowRequest.BeforeImport</c>). A
/// 2.x order has no control (<see cref="For"/> answers <c>null</c>): its levels stay per category,
/// in Revit's checklist column.
/// </para>
/// <para>
/// Every time the control is built for a bundle it starts on the recommendation, including when an
/// export from the web vault opens the window on that bundle: an export names no level, so it is
/// fetched at the recommendation. A window reopened after the curator chose another level is built
/// <see cref="At"/> that level.
/// </para>
/// </remarks>
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

    /// <summary>The bundle the control is for.</summary>
    public VaultBundle Bundle { get; }

    /// <summary>What the bundle's listing row offered.</summary>
    public LevelOffer Offer { get; }

    /// <summary>Max, Med and Min, in that order.</summary>
    public IReadOnlyList<DownloadLevelOption> Options { get; }

    /// <summary>The level the next download asks for.</summary>
    public FidelityLevel Selected { get; private set; }

    /// <summary>The label beside the control.</summary>
    public static string Heading => WindowLabels.DownloadLevelHeading;

    /// <summary>The index of <see cref="Selected"/> in <see cref="Options"/>.</summary>
    public int SelectedIndex => Options.ToList().FindIndex(option => option.Level == Selected);

    /// <summary>
    /// One row for each Extra the platform locks on this bundle, in its order, each with the words that
    /// stand in place of a download. Empty on a kept or paid bundle, and on a row that lists no Extra.
    /// </summary>
    public IReadOnlyList<string> LockedRows
        => [.. Offer.Extras.Where(extra => extra.Locked).Select(extra => WindowLabels.LockedExtraRow(extra.Name))];

    /// <summary>The cache slot level the selected level is downloaded into (<see cref="DownloadLevels.SlotLevel"/>).</summary>
    public string SlotLevel => DownloadLevels.SlotLevel(Bundle, Selected);

    /// <summary>The control for <paramref name="bundle"/>, on the recommendation; <c>null</c> for a 2.x order.</summary>
    public static LevelControl? For(VaultBundle? bundle)
        => bundle?.Levels is { } offer ? new LevelControl(bundle, offer) : null;

    /// <summary>
    /// The control for <paramref name="bundle"/>, on <paramref name="level"/>: the level a window reopened
    /// after a fresh download holds. <c>null</c> for a 2.x order; on the recommendation for a level not offered.
    /// </summary>
    public static LevelControl? At(VaultBundle? bundle, FidelityLevel level)
    {
        LevelControl? control = For(bundle);
        _ = control?.Choose(level);
        return control;
    }

    /// <summary>The curator chooses <paramref name="level"/>. <c>false</c>, and nothing changes, for a level not offered.</summary>
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
