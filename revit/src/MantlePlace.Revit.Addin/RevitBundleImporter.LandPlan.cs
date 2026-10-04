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
        View? existing = NamedAs<View>(viewName).OrderBy(candidate => candidate.IsTemplate).FirstOrDefault();
        PlanViewFound found = existing switch
        {
            null => PlanViewFound.None,
            ViewPlan { IsTemplate: false } => PlanViewFound.PlanView,
            _ => PlanViewFound.SomethingElse,
        };

        List<FilledRegion> onPlan = existing is ViewPlan existingPlan ? RegionsIn(existingPlan) : [];
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

        int drawn = 0;
        int declined = 0;
        int unstamped = 0;
        int handled = 0;
        List<SiteFeature> drawnOuters = [];
        foreach (GroundCut cut in regions.Cuts)
        {
            Count(handled++, regions.Cuts.Count);

            // Every ring or none: a hole that cannot close would leave the region covering ground the
            // class does not, which is a repair by another name. The polygon is refused whole instead.
            List<CurveLoop?> built = [Loop(cut.Outer, z), .. cut.Holes.Select(hole => Loop(hole, z))];
            if (built.Contains(null))
            {
                declined++;
                continue;
            }

            List<CurveLoop> loops = [.. built.OfType<CurveLoop>()];
            try
            {
                FilledRegion region = FilledRegion.Create(
                    _document, RegionType(LandStyles.For(layer, cut.Outer.Subtype), types), plan.Id, loops);
                drawn++;
                drawnOuters.Add(cut.Outer);
                if (!TryStamp(region, stamp))
                {
                    unstamped++;
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                // One region lost, counted and said — never repaired into a shape nobody published.
                declined++;
            }
        }

        Count(regions.Cuts.Count, regions.Cuts.Count);

        // The key names only what is on the plan: a class whose every polygon was refused gets no row.
        IReadOnlyList<KeyRow> rows = drawnOuters.Count == 0
            ? []
            : [.. LandKey.Heading(layer).Select(line => new KeyRow(line, null)), .. LandKey.Rows(layer, drawnOuters)];
        int keyRows = DrawKeyRows(plan, rows, LandPlan.KeyStamp(layer, stem, facts.Build), facts.Crop, drawnExtent, z, types);

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        string summary = $"Drew {drawn:N0} {label} region(s) on \"{viewName}\"";
        if (decision.CreateView)
        {
            summary += facts.Crop is null
                ? ", a new plan left uncropped because this import has no imagery rectangle to crop it to"
                : ", a new plan cropped to the imagery's published rectangle";
        }

        if (declined > 0)
        {
            summary += $"; Revit refused {declined:N0} region(s) as published, and they were skipped rather than repaired";
        }

        if (regions.StrandedHoles > 0)
        {
            summary += $"; {regions.StrandedHoles:N0} hole(s) belong to a polygon whose outer ring could not be read";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        Say(summary + $". The key has {Math.Max(0, keyRows - 1):N0} class(es).");
    }
}
