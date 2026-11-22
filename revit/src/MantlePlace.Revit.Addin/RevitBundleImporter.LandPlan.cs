using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
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
