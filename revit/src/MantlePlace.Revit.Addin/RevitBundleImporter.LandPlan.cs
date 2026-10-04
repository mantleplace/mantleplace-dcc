using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The land plan steps: the land use and the land cover, each drawn as filled regions on a plan of its
// own, with a key quoting the published classes. What is drawn, where, and whether at all is LandPlan's.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// One land layer on its build's land plan: the plan made, the layer's regions drawn and stamped,
    /// and its key drawn beside them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plan view, not the terrain (<c>docs/adr/0015-revit-subdivisions-are-for-built-surfaces.md</c>):
    /// these layers were cut as subdivisions, and were most of a full import's time for an edge line
    /// and a selectable area under the drape. Filled regions cost seconds at any count, sit on a sheet
    /// as they are, and leave the model a visualiser renders untouched.
    /// </para>
    /// <para>
    /// Each polygon is drawn as published, and the plan's crop decides what shows. A region Revit
    /// refuses is skipped and counted, never repaired: repairing a published polygon is derivation. An
    /// empty layer draws nothing and makes no plan. ⛔ A plan is never redrawn: a layer already on this
    /// build's plan is left alone (<see cref="LandPlan.Decide"/>).
    /// </para>
    /// </remarks>
    private void ImportLandPlan(ImportStep step, LandLayer layer)
    {
        if (step.LandPlan is not { } facts)
        {
            return;
        }

        string label = SiteVectorLayers.For(step.Kind).Label;

        if (ReadVectorLayer(step, SiteVectorLayers.For(step.Kind).DrawnFrom, label) is not { } rings)
        {
            return;
        }

        GroundCutPlan regions = LandPlan.Regions(rings);
        if (regions.Cuts.Count == 0)
        {
            Say($"The {label} layer ({step.EntryName}) carries no polygon this plugin could draw, so no land "
                + "plan was made for it.");
            return;
        }

        string stem = _archive.Layout.Key.Stem;
        string viewName = LandPlan.ViewName(layer, facts.Build);
        (View? existing, PlanViewFound found, List<FilledRegion> onPlan) = FindDrawnPlan(viewName);
        PlanDecision decision = LandPlan.Decide(found, onPlan.Select(CommentsOf), layer, stem, facts.Build);
        if (!decision.Draw)
        {
            Say(decision.Explanation);
            return;
        }

        FootprintExtent? drawnExtent = HazardPlan.Around(regions.Cuts.Select(cut => FootprintExtent.Around(cut.Outer.Vertices)));

        // A step never measured says nothing; the core decides from what was.
        Announce(SlowStepNotice.For(step.Kind, null, regions.Cuts.Count));

        ImportFailureSwallower swallower = new($"Drawing the {label}");
        using Transaction transaction = BeginTransaction($"Mantle Place: {label} plan", swallower);

        ViewPlan? plan = decision.CreateView ? CreateDrawnPlan(viewName, facts.Crop ?? drawnExtent) : existing as ViewPlan;
        if (plan is null)
        {
            transaction.RollBack();
            Say($"Skipped the {label}: this project has no level and no floor plan type to make \"{viewName}\" "
                + "on. Add a level and import again.");
            return;
        }

        double z = plan.GenLevel?.Elevation ?? 0.0;
        string stamp = LandPlan.Stamp(layer, stem, facts.Build);
        Dictionary<string, ElementId> types = [];

        DrawnRegions drawn = DrawRegions(plan, regions, z, outer => LandStyles.For(layer, outer.Subtype), stamp, types);
        List<SiteFeature> drawnOuters = drawn.Outers;

        // The key names only what is on the plan: a class whose every polygon was refused gets no row.
        IReadOnlyList<KeyRow> rows = drawnOuters.Count == 0
            ? []
            : [.. LandKey.Heading(layer).Select(line => new KeyRow(line, null)), .. LandKey.Rows(layer, drawnOuters)];
        int keyRows = DrawKeyRows(plan, rows, LandPlan.KeyStamp(layer, stem, facts.Build), facts.Crop, drawnExtent, z, types);

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        string summary = DescribeDrawnPlan(label, viewName, decision.CreateView, facts.Crop, drawn, regions.StrandedHoles);

        Say(summary + $". The key has {Math.Max(0, keyRows - 1):N0} class(es).");
    }
}
