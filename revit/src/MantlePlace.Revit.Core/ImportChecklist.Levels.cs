namespace MantlePlace.Revit.Core;

/// <summary>One choice in a row's level list.</summary>
/// <param name="Level">The level.</param>
/// <param name="Label">What the list says for it (<see cref="WindowLabels.LevelOption"/>).</param>
/// <param name="IsAvailable">Whether this plugin can import it; an unavailable level is listed and cannot be chosen.</param>
public sealed record LevelOption(FidelityLevel Level, string Label, bool IsAvailable);

/// <summary>The checklist's fidelity levels: which rows offer them, and what the curator chose.</summary>
/// <remarks>
/// <para>
/// A row offers levels when the entry its step is planned from publishes a <c>levels</c> block. A
/// row whose entry publishes none is MAX only and offers no list, which is every bundle before
/// MPB 1.9.0 (<see cref="FidelityCuts.Apply"/>).
/// </para>
/// <para>
/// Every row starts at MAX, the entry itself, until measured defaults are set. A level this plugin
/// cannot import is listed so the four always read the same, and choosing it does nothing; the set-all
/// control skips a row whose level it is.
/// </para>
/// </remarks>
public sealed partial class ImportChecklist
{
    private readonly Dictionary<ImportLayer, (ImportStepKind Kind, FidelityLevels Levels)> _offeredLevels = [];
    private readonly Dictionary<ImportLayer, FidelityLevel> _chosenLevels = [];

    /// <summary>Whether any row offers levels — and so whether the window shows the level column at all.</summary>
    public bool OffersLevels => _offeredLevels.Count > 0;

    /// <summary>Whether a row offers a level list.</summary>
    public bool HasLevels(ImportLayer layer) => _offeredLevels.ContainsKey(layer);

    /// <summary>
    /// The four choices a row lists, RAW to MIN, or empty for a row that offers none.
    /// </summary>
    /// <remarks>
    /// A level's count is its own cost driver, or the cost driver of the level it is the same as, so
    /// a MED that is MAX says MAX's count rather than nothing.
    /// </remarks>
    public IReadOnlyList<LevelOption> LevelOptions(ImportLayer layer)
    {
        if (!_offeredLevels.TryGetValue(layer, out (ImportStepKind Kind, FidelityLevels Levels) offered))
        {
            return [];
        }

        return
        [
            .. offered.Levels.All.Select(published =>
            {
                bool available = FidelityCuts.IsAvailable(offered.Levels, published.Level, offered.Kind);
                CostDriver? cost = published.Cost ?? offered.Levels.Resolve(published.Level).Cost;
                SlowStepNotice.LevelEstimate? estimate = SlowStepNotice.EstimateLevel(offered.Kind, cost, _revitVersionNumber);
                return new LevelOption(
                    published.Level,
                    WindowLabels.LevelOption(published.Level, published, available, cost, estimate),
                    available);
            }),
        ];
    }

    /// <summary>The level a row imports at: MAX until the curator chooses another.</summary>
    public FidelityLevel ChosenLevel(ImportLayer layer) => _chosenLevels.GetValueOrDefault(layer, FidelityLevel.Max);

    /// <summary>
    /// Records a row's level. A row that offers none, or a level this plugin cannot import, is
    /// ignored — the list shows it, and choosing it is not a choice.
    /// </summary>
    public void SetLevel(ImportLayer layer, FidelityLevel level)
    {
        if (!_offeredLevels.TryGetValue(layer, out (ImportStepKind Kind, FidelityLevels Levels) offered)
            || !FidelityCuts.IsAvailable(offered.Levels, level, offered.Kind))
        {
            return;
        }

        _chosenLevels[layer] = level;
    }

    /// <summary>Sets every row that offers levels to <paramref name="level"/>, skipping a row where it is unavailable.</summary>
    public void SetAllLevels(FidelityLevel level)
    {
        foreach (ImportLayer layer in _offeredLevels.Keys)
        {
            SetLevel(layer, level);
        }
    }

    /// <summary>The level every row offering levels is at, or <c>null</c> while they differ or none offers any.</summary>
    public FidelityLevel? AllLevels
    {
        get
        {
            List<FidelityLevel> chosen = [.. _offeredLevels.Keys.Select(ChosenLevel).Distinct()];
            return chosen.Count == 1 ? chosen[0] : null;
        }
    }

    /// <summary>The levels as the planner takes them: every row offering levels, at its chosen level.</summary>
    private IReadOnlyDictionary<ImportLayer, FidelityLevel> ChosenLevels
        => _offeredLevels.Keys.ToDictionary(layer => layer, ChosenLevel);

    /// <summary>
    /// Offers a level list on each row whose step's entry publishes levels — the first such step of
    /// the row, in plan order, which is the one the row builds from.
    /// </summary>
    private void OfferLevels(IEnumerable<ImportStep> steps)
    {
        foreach (ImportStep step in steps)
        {
            if (ImportLayers.Of(step.Kind) is { } layer
                && step.Levels is { } levels
                && Layers.Contains(layer)
                && !_offeredLevels.ContainsKey(layer))
            {
                _offeredLevels[layer] = (step.Kind, levels);
            }
        }
    }
}
