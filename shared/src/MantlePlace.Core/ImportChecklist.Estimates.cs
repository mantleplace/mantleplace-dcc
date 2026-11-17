namespace MantlePlace.Core;

/// <summary>The checklist's time estimates for an MPB 3.0 bundle, and the layers they start unticked.</summary>
/// <remarks>
/// <para>
/// A 3.0 download holds one level of every file, so a row has no level list to estimate from. Its
/// estimate is its file's catalog count (<see cref="ImportStep.Cost"/>) times Revit's measured cost
/// per unit (<see cref="SlowStepNotice.EstimateLevel"/>), the sum a 2.x level's estimate is. A 2.x
/// plan's steps carry no count, so nothing here applies to one.
/// </para>
/// <para>
/// A layer whose estimate breaks <see cref="SlowStepNotice.BudgetSeconds"/> starts unticked, and the
/// curator can tick it. Ticking it imports all of it: the plugin never thins or samples a layer to
/// fit the budget, and the plan reads the same file whatever the box says.
/// </para>
/// </remarks>
public sealed partial class ImportChecklist
{
    private readonly Dictionary<ImportLayer, (CostDriver Cost, SlowStepNotice.LevelEstimate Estimate)> _estimates = [];

    /// <summary>
    /// Whether <paramref name="layer"/>'s estimate breaks the budget, which is why it starts
    /// unticked. <c>false</c> for a layer with no estimate.
    /// </summary>
    public bool IsOverBudget(ImportLayer layer)
        => _estimates.TryGetValue(layer, out (CostDriver Cost, SlowStepNotice.LevelEstimate Estimate) found)
           && SlowStepNotice.BreaksBudget(found.Estimate);

    /// <summary>
    /// What the window says beside <paramref name="layer"/>'s box: its estimate, and where it breaks
    /// the budget, why the box starts unticked. <c>null</c> for a layer with no estimate, which is
    /// every layer of a 2.x bundle.
    /// </summary>
    public string? EstimateNote(ImportLayer layer)
        => _estimates.TryGetValue(layer, out (CostDriver Cost, SlowStepNotice.LevelEstimate Estimate) found)
            ? WindowLabels.EstimateNote(found.Cost, found.Estimate, SlowStepNotice.BreaksBudget(found.Estimate))
            : null;

    /// <summary>
    /// Estimates each row from its step's catalog count, the first step of the row in plan order
    /// (the one it builds from), and starts the rows over the budget unticked.
    /// </summary>
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

    /// <summary>
    /// Unticks every row over the budget. Run after a remembered choice has been applied: what the
    /// curator ticked for another bundle says nothing about this bundle's size.
    /// </summary>
    private void UntickOverBudget()
    {
        foreach (ImportLayer layer in Layers.Where(IsOverBudget))
        {
            _wanted.Remove(layer);
        }
    }
}
