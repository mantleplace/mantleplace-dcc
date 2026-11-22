namespace MantlePlace.Core;

public sealed record LevelOption(FidelityLevel Level, string Label, bool IsAvailable);

public sealed partial class ImportChecklist
{
    private readonly Dictionary<ImportLayer, (ImportStepKind Kind, FidelityLevels Levels)> _offeredLevels = [];
    private readonly Dictionary<ImportLayer, FidelityLevel> _chosenLevels = [];

    public bool OffersLevels => _offeredLevels.Count > 0;

    public bool HasLevels(ImportLayer layer) => _offeredLevels.ContainsKey(layer);

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

    public FidelityLevel ChosenLevel(ImportLayer layer)
    {
        if (_chosenLevels.TryGetValue(layer, out FidelityLevel chosen))
        {
            return chosen;
        }

        FidelityLevel preferred = ImportLayers.DefaultLevel(layer);
        return _offeredLevels.TryGetValue(layer, out (ImportStepKind Kind, FidelityLevels Levels) offered)
               && FidelityCuts.IsAvailable(offered.Levels, preferred, offered.Kind)
            ? preferred
            : FidelityLevel.Max;
    }

    public void SetLevel(ImportLayer layer, FidelityLevel level)
    {
        if (!_offeredLevels.TryGetValue(layer, out (ImportStepKind Kind, FidelityLevels Levels) offered)
            || !FidelityCuts.IsAvailable(offered.Levels, level, offered.Kind))
        {
            return;
        }

        _chosenLevels[layer] = level;
    }

    public void SetAllLevels(FidelityLevel level)
    {
        foreach (ImportLayer layer in _offeredLevels.Keys)
        {
            SetLevel(layer, level);
        }
    }

    public FidelityLevel? AllLevels
    {
        get
        {
            List<FidelityLevel> chosen = [.. _offeredLevels.Keys.Select(ChosenLevel).Distinct()];
            return chosen.Count == 1 ? chosen[0] : null;
        }
    }

    private IReadOnlyDictionary<ImportLayer, FidelityLevel> ChosenLevels
        => _offeredLevels.Keys.ToDictionary(layer => layer, ChosenLevel);

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
