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
/// whichever tier the planner picks. <see cref="ImportStepKind.SetSharedCoordinates"/> builds
/// nothing, so it is no layer at all and runs whatever is chosen; nor is
/// <see cref="ImportStepKind.AttributionAndProvenance"/>, because crediting the data that came in is
/// the licence's condition on importing any of it, not a thing to leave out.
/// </para>
/// </remarks>
public enum ImportLayer
{
    Terrain,
    ContextBuildings,

    /// <summary>The site model as a link — the same buildings as <see cref="ContextBuildings"/>, unselectable.</summary>
    SiteModel,
    RoadCentrelines,
    LandUseSubdivisions,
    LandCoverSubdivisions,
    WaterSubdivisions,
    RoadSubdivisions,

    /// <summary>The tree points, of every foliage type: Revit's Planting category, and the host's own noun.</summary>
    Planting,
    ImageryDrape,
}

/// <summary>Properties of an <see cref="ImportLayer"/> that the window must not decide for itself.</summary>
public static class ImportLayers
{
    /// <summary>The layer a step builds, or <c>null</c> for a step that runs whatever is chosen.</summary>
    public static ImportLayer? Of(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile => ImportLayer.Terrain,
        ImportStepKind.ToposurfaceFromSurfaceTin => ImportLayer.Terrain,
        ImportStepKind.ToposurfaceFromSurfaceDxf => ImportLayer.Terrain,
        ImportStepKind.ContextBuildings => ImportLayer.ContextBuildings,
        ImportStepKind.LinkSiteIfc => ImportLayer.SiteModel,
        ImportStepKind.RoadCentrelines => ImportLayer.RoadCentrelines,
        ImportStepKind.SiteBoundaries => ImportLayer.LandUseSubdivisions,
        ImportStepKind.LandCover => ImportLayer.LandCoverSubdivisions,
        ImportStepKind.Water => ImportLayer.WaterSubdivisions,
        ImportStepKind.RoadPolygons => ImportLayer.RoadSubdivisions,
        ImportStepKind.Vegetation => ImportLayer.Planting,
        ImportStepKind.ImageryDrape => ImportLayer.ImageryDrape,
        _ => null,
    };

    /// <summary>
    /// The layer this one cannot be imported without, or <c>null</c> for one that stands alone.
    /// </summary>
    /// <remarks>
    /// Every kind of subdivision is cut into the ground and the drape is a material the ground
    /// wears, so all of them need the terrain. Road centrelines, trees and context buildings carry
    /// their own Z and do not; the site model is a link.
    /// </remarks>
    public static ImportLayer? PrerequisiteOf(ImportLayer layer) => layer switch
    {
        ImportLayer.LandUseSubdivisions => ImportLayer.Terrain,
        ImportLayer.LandCoverSubdivisions => ImportLayer.Terrain,
        ImportLayer.WaterSubdivisions => ImportLayer.Terrain,
        ImportLayer.RoadSubdivisions => ImportLayer.Terrain,
        ImportLayer.ImageryDrape => ImportLayer.Terrain,
        _ => null,
    };

    /// <summary>Whether a layer's box starts checked.</summary>
    /// <remarks>
    /// <para>
    /// Every layer but the site model's link. Its buildings are copied into the project as
    /// <see cref="ImportLayer.ContextBuildings"/>, and a link as well shows each one twice, once
    /// selectable and once not — the stated reason <c>HPS-51</c> asks for before a row starts
    /// unchecked (<c>docs/adr/0012-context-buildings-come-from-the-site-model.md</c>).
    /// </para>
    /// <para>
    /// The unattended path does not read it: it imports everything
    /// (<see cref="ImportLayerChoice.All"/>), the link included, because the standard says an import
    /// nobody is there to choose for brings in everything.
    /// </para>
    /// </remarks>
    public static bool OnByDefault(ImportLayer layer) => layer != ImportLayer.SiteModel;
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
/// unchecks and disables both kinds of subdivision and the drape; checking it again gives back whatever they
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

    /// <summary>
    /// Records a change to a layer's box, however it was made. A write to a box that is not offered,
    /// or is disabled, does nothing.
    /// </summary>
    /// <remarks>
    /// A disabled box is one nobody can toggle, so a write to it is not a choice. Keeping it out of
    /// what the curator chose is what lets checking the prerequisite again give that choice back,
    /// whatever the caller wrote in between.
    /// </remarks>
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
}
