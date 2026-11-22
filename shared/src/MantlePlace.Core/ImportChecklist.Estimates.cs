namespace MantlePlace.Core;

public sealed partial class ImportChecklist
{
    private readonly Dictionary<ImportLayer, (CostDriver Cost, SlowStepNotice.LevelEstimate Estimate)> _estimates = [];

    public bool IsOverBudget(ImportLayer layer)
        => _estimates.TryGetValue(layer, out (CostDriver Cost, SlowStepNotice.LevelEstimate Estimate) found)
           && SlowStepNotice.BreaksBudget(found.Estimate);

    public string? EstimateNote(ImportLayer layer)
        => _estimates.TryGetValue(layer, out (CostDriver Cost, SlowStepNotice.LevelEstimate Estimate) found)
            ? WindowLabels.EstimateNote(found.Cost, found.Estimate, SlowStepNotice.BreaksBudget(found.Estimate))
            : null;

    private void OfferEstimates(IEnumerable<ImportStep> steps)
    {
        foreach (ImportStep step in steps)
        {
            if (ImportLayers.Of(step.Kind) is { } layer
                && step.Cost is { } cost
                && Layers.Contains(layer)
                && !_estimates.ContainsKey(layer)
                && SlowStepNotice.EstimateLevel(step.Kind, cost, _revitVersionNumber) is { } estimate)
            {
                _estimates[layer] = (cost, estimate);
            }
        }
    }

    private void UntickOverBudget()
    {
        foreach (ImportLayer layer in Layers.Where(IsOverBudget))
        {
            _wanted.Remove(layer);
        }
    }
}
