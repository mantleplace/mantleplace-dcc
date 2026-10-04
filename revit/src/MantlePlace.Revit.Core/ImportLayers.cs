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

    /// <summary>The contour linework the order was built with — not the toposolid's own contours.</summary>
    PublishedContours,
    ContextBuildings,

    /// <summary>The site model as a link — the same buildings as <see cref="ContextBuildings"/>, unselectable.</summary>
    SiteModel,
    RoadCentrelines,
    WaterSubdivisions,
    RoadSubdivisions,

    /// <summary>The tree points, of every foliage type: Revit's Planting category, and the host's own noun.</summary>
    Planting,

    /// <summary>The land use, drawn on a land plan of its own.</summary>
    LandUsePlan,

    /// <summary>The land cover, drawn on a land plan of its own.</summary>
    LandCoverPlan,

    /// <summary>The flood map's zones, on the hazard plan.</summary>
    FloodZones,

    /// <summary>Steep ground at the published threshold, on the hazard plan.</summary>
    SteepGround,
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

    /// <summary>
    /// The layer this one cannot be imported without, or <c>null</c> for one that stands alone.
    /// </summary>
    /// <remarks>
    /// Both kinds of subdivision are cut into the ground and the drape is a material the ground
    /// wears, so all of them need the terrain. Road centrelines, trees and context buildings carry
    /// their own Z and do not; the site model is a link. Neither land plan nor hazard layer does
    /// either: each is drawn flat in a plan view, which needs a level and no toposolid.
    /// </remarks>
    public static ImportLayer? PrerequisiteOf(ImportLayer layer) => layer switch
    {
        ImportLayer.WaterSubdivisions => ImportLayer.Terrain,
        ImportLayer.RoadSubdivisions => ImportLayer.Terrain,
        ImportLayer.ImageryDrape => ImportLayer.Terrain,
        _ => null,
    };

    /// <summary>Whether a layer's box starts checked.</summary>
    /// <remarks>
    /// <para>
    /// Every layer but seven, each with the stated reason <c>HPS-51</c> asks for before a row starts
    /// unchecked. The two land plans and the two hazard layers: the curator this import is made for
    /// is the visualiser presenting a render, and a drawn plan is for someone planning the site; each
    /// is one tick away, and none touches the model a render is made from.
    /// </para>
    /// <para>
    /// The site model's link: its buildings are copied into the project as
    /// <see cref="ImportLayer.ContextBuildings"/>, and a link as well shows each one twice, once
    /// selectable and once not (<c>docs/adr/0012-context-buildings-come-from-the-site-model.md</c>).
    /// The published contours: the toposolid already draws contours of its own, so the published
    /// ones are for a curator who wants the order's linework beside them
    /// (<c>docs/adr/0013-revit-published-contours-are-directshapes.md</c>).
    /// </para>
    /// <para>
    /// The road surfaces, the box <see cref="SlowStepNotice.IsSlowBox"/> names: on the order it was
    /// measured on, it was most of what a full import with every box ticked took beyond the default
    /// import (the figures, through the import window, are <c>revit/README.md</c>'s; the box's own is
    /// <see cref="SlowStepNotice.Measured"/>'s). A curator who wants it ticks it, and the checklist
    /// then says what it was measured to cost (<see cref="ImportChecklist.SlowLayerWarnings"/>). The
    /// water bodies stay ticked: they took seconds.
    /// </para>
    /// <para>
    /// The unattended path does not read it: it imports everything
    /// (<see cref="ImportLayerChoice.All"/>), the link and the slow layers included, because the
    /// standard says an import nobody is there to choose for brings in everything.
    /// </para>
    /// </remarks>
    public static bool OnByDefault(ImportLayer layer)
        => layer is not (ImportLayer.SiteModel or ImportLayer.PublishedContours
                or ImportLayer.LandUsePlan or ImportLayer.LandCoverPlan
                or ImportLayer.FloodZones or ImportLayer.SteepGround)
            && !SlowStepNotice.IsSlowBox(layer);
}

/// <summary>Which layers an import brings in, and at which fidelity level each. Immutable.</summary>
public sealed class ImportLayerChoice
{
    private readonly HashSet<ImportLayer> _layers;
    private readonly Dictionary<ImportLayer, FidelityLevel> _levels;

    private ImportLayerChoice(IEnumerable<ImportLayer> layers, IReadOnlyDictionary<ImportLayer, FidelityLevel>? levels)
    {
        _layers = [.. layers];
        _levels = levels is null ? [] : new Dictionary<ImportLayer, FidelityLevel>(levels);
    }

    /// <summary>Every layer, at MAX — what an import with no one there to choose brings in.</summary>
    public static ImportLayerChoice All { get; } = new(Enum.GetValues<ImportLayer>(), null);

    /// <summary>Exactly these layers, each at MAX.</summary>
    public static ImportLayerChoice Only(IEnumerable<ImportLayer> layers) => Only(layers, null);

    /// <summary>Exactly these layers, at the levels given; a layer not given one is at MAX.</summary>
    public static ImportLayerChoice Only(IEnumerable<ImportLayer> layers, IReadOnlyDictionary<ImportLayer, FidelityLevel>? levels)
    {
        ArgumentNullException.ThrowIfNull(layers);
        return new ImportLayerChoice(layers, levels);
    }

    public bool Includes(ImportLayer layer) => _layers.Contains(layer);

    /// <summary>The level chosen for <paramref name="layer"/>: MAX unless another was chosen.</summary>
    public FidelityLevel LevelOf(ImportLayer layer) => _levels.GetValueOrDefault(layer, FidelityLevel.Max);
}

/// <summary>
/// Rows a bundle holds and an import cannot offer, for one reason: what the import window lists
/// below its checklist.
/// </summary>
/// <param name="Layers">The rows, in the order the steps run.</param>
/// <param name="Reason">One sentence over all of them, in the curator's register (<see cref="WindowLabels.UnavailableReason"/>).</param>
public sealed record UnavailableLayers(IReadOnlyList<ImportLayer> Layers, string Reason);

/// <summary>
/// The import window's checklist before the steps run: which layers the bundle carries, which are
/// checked, which cannot be because what they need is not, whether anything is left to import, and
/// which the bundle holds and cannot offer, and why.
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
public sealed partial class ImportChecklist
{
    private readonly HashSet<ImportLayer> _wanted;
    private readonly string? _revitVersionNumber;

    /// <param name="carried">The layers the bundle has a step for. Nothing else is offered.</param>
    public ImportChecklist(IEnumerable<ImportLayer> carried)
        : this(carried, [])
    {
    }

    /// <param name="carried">The layers the bundle has a step for. Nothing else is offered.</param>
    /// <param name="skipped">The plan's skips, which say why a layer that is not offered is not.</param>
    /// <param name="revitVersionNumber">
    /// <c>Application.VersionNumber</c>, which picks the measurement a slow box warns with
    /// (<see cref="SlowLayerWarnings"/>); <c>null</c> when it is not known.
    /// </param>
    public ImportChecklist(IEnumerable<ImportLayer> carried, IEnumerable<SkippedImport> skipped, string? revitVersionNumber = null)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(skipped);

        Layers = [.. carried.Distinct().Order()];
        _wanted = [.. Layers.Where(ImportLayers.OnByDefault)];
        _revitVersionNumber = revitVersionNumber;
        Unavailable = UnavailableFrom(Layers, skipped);
    }

    /// <summary>The checklist for everything a plan made with <see cref="ImportLayerChoice.All"/> would build.</summary>
    /// <param name="plan">The plan made with every layer.</param>
    /// <param name="revitVersionNumber"><c>Application.VersionNumber</c>, or <c>null</c> when it is not known.</param>
    public static ImportChecklist For(BundleImportPlan plan, string? revitVersionNumber = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ImportChecklist checklist = new(
            plan.Steps.Select(step => ImportLayers.Of(step.Kind)).OfType<ImportLayer>(),
            plan.Skipped,
            revitVersionNumber);
        checklist.OfferLevels(plan.Steps);
        return checklist;
    }

    /// <summary>
    /// What the window says below the rows while a slow box is ticked: one line per ticked slow box,
    /// in the order the rows run, each with what it was measured to cost in this Revit. Empty when
    /// none is ticked, which is how the window opens.
    /// </summary>
    /// <remarks>
    /// Only a box that shows ticked warns, so unticking the terrain takes its dependents' warnings
    /// with it and ticking it again gives them back with the ticks. The words and the figures are
    /// <see cref="SlowStepNotice.ForTickedBox"/>'s.
    /// </remarks>
    public IReadOnlyList<string> SlowLayerWarnings
        => [.. Layers.Where(IsChecked).Select(layer => SlowStepNotice.ForTickedBox(layer, _revitVersionNumber)).OfType<string>()];

    /// <summary>The rows, in the order the steps run.</summary>
    public IReadOnlyList<ImportLayer> Layers { get; }

    /// <summary>
    /// What the window lists below the rows: each layer the bundle holds and this import cannot
    /// offer, grouped under one sentence per reason, in the order the steps run. Empty for a bundle
    /// with nothing withheld, which is what a current bundle should be.
    /// </summary>
    /// <remarks>
    /// Never a box: nothing here can be ticked. The list is said before any step runs because the
    /// same reasons, in the planner's words, otherwise reach the curator only in the closing report.
    /// </remarks>
    public IReadOnlyList<UnavailableLayers> Unavailable { get; }

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
    public ImportLayerChoice Choice => ImportLayerChoice.Only(Layers.Where(IsChecked), ChosenLevels);

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

    /// <summary>Which of the layers not offered the window lists, and under which sentence.</summary>
    /// <remarks>
    /// <para>
    /// One reason per layer: the first of its skips, in plan order, that the window has a sentence
    /// for — and a skip about a file the bundle holds before one about a file it lacks, which is
    /// the weakest thing to say. A terrain no tier could build carries a skip per tier: the TIN's
    /// absence comes first in plan order and is not why the points file failed, and the suppressed
    /// fallback's is silent because the skip that suppressed it is the reason.
    /// </para>
    /// <para>
    /// Layers are grouped by the sentence they would each be said under alone, so two codes that
    /// tell the curator the same thing are one sentence over all their rows, not two identical ones.
    /// </para>
    /// </remarks>
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
