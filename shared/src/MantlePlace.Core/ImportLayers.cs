namespace MantlePlace.Core;

public enum ImportLayer
{
    Terrain,

    PublishedContours,
    ContextBuildings,

    SiteModel,
    RoadCentrelines,
    WaterSubdivisions,
    RoadSubdivisions,

    Planting,

    LandUsePlan,

    LandCoverPlan,

    FloodZones,

    SteepGround,
    ImageryDrape,
}

public static class ImportLayers
{
    public static ImportLayer? Of(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile => ImportLayer.Terrain,
        ImportStepKind.ToposurfaceFromSurfaceTin => ImportLayer.Terrain,
        ImportStepKind.ToposurfaceFromSurfaceDxf => ImportLayer.Terrain,
        ImportStepKind.PublishedContours => ImportLayer.PublishedContours,
        ImportStepKind.ContextBuildings => ImportLayer.ContextBuildings,
        ImportStepKind.LinkSiteIfc => ImportLayer.SiteModel,
        ImportStepKind.RoadCentrelines => ImportLayer.RoadCentrelines,
        ImportStepKind.LandUse => ImportLayer.LandUsePlan,
        ImportStepKind.LandCover => ImportLayer.LandCoverPlan,
        ImportStepKind.Water => ImportLayer.WaterSubdivisions,
        ImportStepKind.RoadPolygons => ImportLayer.RoadSubdivisions,
        ImportStepKind.Vegetation => ImportLayer.Planting,
        ImportStepKind.FloodZones => ImportLayer.FloodZones,
        ImportStepKind.SteepGround => ImportLayer.SteepGround,
        ImportStepKind.ImageryDrape => ImportLayer.ImageryDrape,
        _ => null,
    };

    public static ImportLayer? PrerequisiteOf(ImportLayer layer) => layer switch
    {
        ImportLayer.WaterSubdivisions => ImportLayer.Terrain,
        ImportLayer.RoadSubdivisions => ImportLayer.Terrain,
        ImportLayer.ImageryDrape => ImportLayer.Terrain,
        _ => null,
    };

    public static bool OnByDefault(ImportLayer layer)
        => layer is not ImportLayer.SiteModel && !SlowStepNotice.IsSlowBox(layer);

    public static FidelityLevel DefaultLevel(ImportLayer layer) => layer switch
    {
        ImportLayer.Planting => FidelityLevel.Min,
        ImportLayer.RoadSubdivisions => FidelityLevel.Min,
        _ => FidelityLevel.Max,
    };
}

public sealed class ImportLayerChoice
{
    private readonly HashSet<ImportLayer> _layers;
    private readonly Dictionary<ImportLayer, FidelityLevel> _levels;

    private ImportLayerChoice(IEnumerable<ImportLayer> layers, IReadOnlyDictionary<ImportLayer, FidelityLevel>? levels)
    {
        _layers = [.. layers];
        _levels = levels is null ? [] : new Dictionary<ImportLayer, FidelityLevel>(levels);
    }

    public static ImportLayerChoice All { get; } = new(Enum.GetValues<ImportLayer>(), null);

    public static ImportLayerChoice Only(IEnumerable<ImportLayer> layers) => Only(layers, null);

    public static ImportLayerChoice Only(IEnumerable<ImportLayer> layers, IReadOnlyDictionary<ImportLayer, FidelityLevel>? levels)
    {
        ArgumentNullException.ThrowIfNull(layers);
        return new ImportLayerChoice(layers, levels);
    }

    public bool Includes(ImportLayer layer) => _layers.Contains(layer);

    public FidelityLevel LevelOf(ImportLayer layer) => _levels.GetValueOrDefault(layer, FidelityLevel.Max);
}

public sealed record UnavailableLayers(IReadOnlyList<ImportLayer> Layers, string Reason);

public sealed partial class ImportChecklist
{
    private readonly HashSet<ImportLayer> _wanted;
    private readonly string? _revitVersionNumber;

    public ImportChecklist(IEnumerable<ImportLayer> carried)
        : this(carried, [])
    {
    }

    public ImportChecklist(IEnumerable<ImportLayer> carried, IEnumerable<SkippedImport> skipped, string? revitVersionNumber = null)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(skipped);

        Layers = [.. carried.Distinct().Order()];
        _wanted = [.. Layers.Where(ImportLayers.OnByDefault)];
        _revitVersionNumber = revitVersionNumber;
        Unavailable = UnavailableFrom(Layers, skipped);
    }

    public static ImportChecklist For(BundleImportPlan plan, string? revitVersionNumber = null, RememberedChoice? remembered = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ImportChecklist checklist = new(
            plan.Steps.Select(step => ImportLayers.Of(step.Kind)).OfType<ImportLayer>(),
            plan.Skipped,
            revitVersionNumber);
        checklist.OfferLevels(plan.Steps);
        checklist.OfferEstimates(plan.Steps);
        checklist.Restore(remembered);
        checklist.UntickOverBudget();
        return checklist;
    }

    public IReadOnlyList<string> SlowLayerWarnings
        => [.. Layers.Where(IsChecked).Select(layer => SlowStepNotice.ForTickedBox(layer, _revitVersionNumber)).OfType<string>()];

    public IReadOnlyList<ImportLayer> Layers { get; }

    public IReadOnlyList<UnavailableLayers> Unavailable { get; }

    public ImportLayer? MissingPrerequisite(ImportLayer layer)
        => ImportLayers.PrerequisiteOf(layer) is { } prerequisite
           && Layers.Contains(prerequisite)
           && !IsChecked(prerequisite)
            ? prerequisite
            : null;

    public bool IsEnabled(ImportLayer layer) => Layers.Contains(layer) && MissingPrerequisite(layer) is null;

    public bool IsChecked(ImportLayer layer) => _wanted.Contains(layer) && IsEnabled(layer);

    public bool CanImport => Layers.Any(IsChecked);

    public ImportLayerChoice Choice => ImportLayerChoice.Only(Layers.Where(IsChecked), ChosenLevels);

    public void Set(ImportLayer layer, bool on)
    {
        if (!IsEnabled(layer))
        {
            return;
        }

        if (on)
        {
            _wanted.Add(layer);
        }
        else
        {
            _wanted.Remove(layer);
        }
    }

    private static List<UnavailableLayers> UnavailableFrom(IReadOnlyList<ImportLayer> offered, IEnumerable<SkippedImport> skipped)
    {
        List<SkippedImport> skips = [.. skipped];
        List<(ImportLayer Layer, SkipReasonCode Code)> withheld = [];

        foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
        {
            if (offered.Contains(layer))
            {
                continue;
            }

            SkippedImport? reason = skips
                .Where(skip => ImportLayers.Of(skip.Kind) == layer && WindowLabels.UnavailableReason(skip.ReasonCode, 1) is not null)
                .OrderBy(skip => skip.ReasonCode == SkipReasonCode.ArtifactNotInManifest)
                .FirstOrDefault();
            if (reason is not null)
            {
                withheld.Add((layer, reason.ReasonCode));
            }
        }

        return
        [
            .. withheld
                .GroupBy(entry => WindowLabels.UnavailableReason(entry.Code, 1), StringComparer.Ordinal)
                .Select(group => new UnavailableLayers(
                    [.. group.Select(entry => entry.Layer)],
                    WindowLabels.UnavailableReason(group.First().Code, group.Count())!)),
        ];
    }
}
