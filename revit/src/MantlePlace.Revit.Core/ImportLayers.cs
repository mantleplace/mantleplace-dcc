namespace MantlePlace.Revit.Core;

/// <summary>
/// One thing a bundle import builds that a curator can leave out — a row of the import window's
/// checklist.
/// </summary>
/// <remarks>
/// <para>
/// Declared in the order the steps run, and the checklist lists them in this order, so the list a
/// curator ticks and the steps they then watch read top to bottom the same way.
/// </para>
/// <para>
/// Not a step kind. Three kinds build the one terrain, and a curator choosing it is choosing
/// whichever tier the planner picks; <see cref="ImportStepKind.SetSharedCoordinates"/> builds
/// nothing, so it is no layer at all and runs whatever is chosen.
/// </para>
/// </remarks>
public enum ImportLayer
{
    Terrain,
    SiteModel,
    RoadCentrelines,
    Subdivisions,
    Trees,
    ImageryDrape,
}

/// <summary>Properties of an <see cref="ImportLayer"/> that the window must not decide for itself.</summary>
public static class ImportLayers
{
    /// <summary>The layer a step builds, or <c>null</c> for the one step that builds nothing.</summary>
    public static ImportLayer? Of(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile => ImportLayer.Terrain,
        ImportStepKind.ToposurfaceFromSurfaceTin => ImportLayer.Terrain,
        ImportStepKind.ToposurfaceFromSurfaceDxf => ImportLayer.Terrain,
        ImportStepKind.LinkSiteIfc => ImportLayer.SiteModel,
        ImportStepKind.RoadCentrelines => ImportLayer.RoadCentrelines,
        ImportStepKind.SiteBoundaries => ImportLayer.Subdivisions,
        ImportStepKind.Vegetation => ImportLayer.Trees,
        ImportStepKind.ImageryDrape => ImportLayer.ImageryDrape,
        _ => null,
    };

    /// <summary>
    /// The layer this one cannot be imported without, or <c>null</c> for one that stands alone.
    /// </summary>
    /// <remarks>
    /// Subdivisions are cut into the ground and the drape is a material the ground wears, so both
    /// need the terrain. Roads and trees carry their own Z and do not; the site model is a link.
    /// </remarks>
    public static ImportLayer? PrerequisiteOf(ImportLayer layer) => layer switch
    {
        ImportLayer.Subdivisions => ImportLayer.Terrain,
        ImportLayer.ImageryDrape => ImportLayer.Terrain,
        _ => null,
    };

    /// <summary>Whether a layer's box starts checked.</summary>
    /// <remarks>
    /// Every layer today. The site model leaves the default once context buildings are copied out of
    /// it, and this is the one place that changes. The unattended path does not read it: it imports
    /// everything (<see cref="ImportLayerChoice.All"/>).
    /// </remarks>
    public static bool OnByDefault(ImportLayer layer) => true;
}

/// <summary>Which layers an import brings in. Immutable.</summary>
public sealed class ImportLayerChoice
{
    private readonly HashSet<ImportLayer> _layers;

    private ImportLayerChoice(IEnumerable<ImportLayer> layers) => _layers = [.. layers];

    /// <summary>Every layer — what an import with no one there to choose brings in.</summary>
    public static ImportLayerChoice All { get; } = new(Enum.GetValues<ImportLayer>());

    /// <summary>Exactly these layers.</summary>
    public static ImportLayerChoice Only(IEnumerable<ImportLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        return new ImportLayerChoice(layers);
    }

    public bool Includes(ImportLayer layer) => _layers.Contains(layer);
}

/// <summary>
/// The import window's checklist before the steps run: which layers the bundle carries, which are
/// checked, which cannot be because what they need is not, and whether anything is left to import.
/// </summary>
/// <remarks>
/// <para>
/// The window binds a check box to each of <see cref="Layers"/> and does nothing else. What a box
/// shows is decided here, where a test reaches it (<c>HPS-02</c>).
/// </para>
/// <para>
/// A box remembers what the curator set it to while its prerequisite is off. Unchecking the terrain
/// unchecks and disables the subdivisions and the drape; checking it again gives back whatever they
/// were, rather than making the curator redo them.
/// </para>
/// </remarks>
public sealed class ImportChecklist
{
    private readonly HashSet<ImportLayer> _wanted;

    /// <param name="carried">The layers the bundle has a step for. Nothing else is offered.</param>
    public ImportChecklist(IEnumerable<ImportLayer> carried)
    {
        ArgumentNullException.ThrowIfNull(carried);

        Layers = [.. carried.Distinct().Order()];
        _wanted = [.. Layers.Where(ImportLayers.OnByDefault)];
    }

    /// <summary>The checklist for everything a plan made with <see cref="ImportLayerChoice.All"/> would build.</summary>
    public static ImportChecklist For(BundleImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new ImportChecklist(plan.Steps.Select(step => ImportLayers.Of(step.Kind)).OfType<ImportLayer>());
    }

    /// <summary>The rows, in the order the steps run.</summary>
    public IReadOnlyList<ImportLayer> Layers { get; }

    /// <summary>
    /// The prerequisite a layer needs and does not have, or <c>null</c>. What the window writes beside
    /// a disabled box.
    /// </summary>
    /// <remarks>
    /// A prerequisite the bundle does not carry is not missing in this sense: there is no box to
    /// check, so disabling the dependent would leave a row nobody could ever light. Its step finds the
    /// project's own ground, as it did before there was a checklist.
    /// </remarks>
    public ImportLayer? MissingPrerequisite(ImportLayer layer)
        => ImportLayers.PrerequisiteOf(layer) is { } prerequisite
           && Layers.Contains(prerequisite)
           && !IsChecked(prerequisite)
            ? prerequisite
            : null;

    /// <summary>Whether a layer's box can be toggled.</summary>
    public bool IsEnabled(ImportLayer layer) => Layers.Contains(layer) && MissingPrerequisite(layer) is null;

    /// <summary>Whether a layer's box shows checked — and so whether it is imported.</summary>
    public bool IsChecked(ImportLayer layer) => _wanted.Contains(layer) && IsEnabled(layer);

    /// <summary>Whether Import is lit: at least one layer would be built.</summary>
    public bool CanImport => Layers.Any(IsChecked);

    /// <summary>What the curator has chosen, as the planner takes it.</summary>
    public ImportLayerChoice Choice => ImportLayerChoice.Only(Layers.Where(IsChecked));

    /// <summary>Records a click on a layer's box. A click on a box that is not offered does nothing.</summary>
    public void Set(ImportLayer layer, bool on)
    {
        if (!Layers.Contains(layer))
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
}
