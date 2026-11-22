using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private const double HatchSpacingMm = 2.0;

    private void ImportHazardLayer(ImportStep step, HazardLayer layer)
    {
        if (step.Hazard is not { } facts)
        {
            return;
        }

        bool flood = layer == HazardLayer.FloodZones;
        string label = SiteVectorLayers.For(step.Kind).Label;

        if (ReadVectorLayer(step, SiteVectorLayers.For(step.Kind).DrawnFrom, label) is not { } rings)
        {
            return;
        }

        GroundCutPlan regions = HazardPlan.Regions(rings);
        if (regions.Cuts.Count == 0)
        {
            Say($"The {label} layer ({step.EntryName}) carries no polygon this plugin could draw, so no hazard "
                + "plan was made for it.");
            return;
        }

        string stem = _archive.Layout.Key.Stem;
        string viewName = HazardPlan.ViewName(facts.Build);
        (View? existing, PlanViewFound found, List<FilledRegion> onPlan) = FindDrawnPlan(viewName);
        PlanDecision decision = HazardPlan.Decide(found, onPlan.Select(CommentsOf), layer, stem, facts.Build);
        if (!decision.Draw)
        {
            Say(decision.Explanation);
            return;
        }

        FootprintExtent? drawnExtent = HazardPlan.Around(regions.Cuts.Select(cut => FootprintExtent.Around(cut.Outer.Vertices)));

        Announce(SlowStepNotice.ForHazardLayer(step.Kind, decision.CreateView, regions.Cuts.Count));

        ImportFailureSwallower swallower = new($"Drawing the {label}");
        using Transaction transaction = BeginTransaction($"Mantle Place: {label}", swallower);

        ViewPlan? plan = decision.CreateView ? CreateDrawnPlan(viewName, facts.Crop ?? drawnExtent) : existing as ViewPlan;
        if (plan is null)
        {
            transaction.RollBack();
            Say($"Skipped the {label}: this project has no level and no floor plan type to make \"{viewName}\" "
                + "on. Add a level and import again.");
            return;
        }

        double z = plan.GenLevel?.Elevation ?? 0.0;
        string stamp = HazardPlan.Stamp(layer, stem, facts.Build);
        Dictionary<string, ElementId> types = [];

        DrawnRegions drawn = DrawRegions(
            plan,
            regions,
            z,
            outer => flood ? HazardStyles.ForFloodZone(outer.FloodZone, outer.FloodZoneSubtype) : HazardStyles.SteepGround,
            stamp,
            types);
        List<SiteFeature> drawnOuters = drawn.Outers;

        IReadOnlyList<KeyRow> rows = drawnOuters.Count == 0
            ? []
            : flood
                ? [.. ZoneKey.FloodHeading(facts.FloodMap).Select(line => new KeyRow(line, null)), .. ZoneKey.FloodRows(drawnOuters, facts.FloodMap)]
                : ZoneKey.SteepRows(drawnOuters, facts.Threshold);
        int keyRows = DrawKeyRows(plan, rows, HazardPlan.KeyStamp(stem, facts.Build), facts.Crop, drawnExtent, z, types);

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        string summary = DescribeDrawnPlan(label, viewName, decision.CreateView, facts.Crop, drawn, regions.StrandedHoles);

        Say(summary + $". The zone key gained {keyRows:N0} row(s). These are context, not a determination.");
    }

    private (View? Existing, PlanViewFound Found, List<FilledRegion> OnPlan) FindDrawnPlan(string viewName)
    {
        View? existing = NamedAs<View>(viewName).OrderBy(candidate => candidate.IsTemplate).FirstOrDefault();
        PlanViewFound found = existing switch
        {
            null => PlanViewFound.None,
            ViewPlan { IsTemplate: false } => PlanViewFound.PlanView,
            _ => PlanViewFound.SomethingElse,
        };

        return (existing, found, existing is ViewPlan existingPlan ? RegionsIn(existingPlan) : []);
    }

    private sealed record DrawnRegions(int Count, int Declined, int Unstamped, List<SiteFeature> Outers);

    private DrawnRegions DrawRegions(
        ViewPlan plan,
        GroundCutPlan regions,
        double z,
        Func<SiteFeature, RegionStyle> styleOf,
        string stamp,
        Dictionary<string, ElementId> types)
    {
        int drawn = 0;
        int declined = 0;
        int unstamped = 0;
        int handled = 0;
        List<SiteFeature> outers = [];
        foreach (GroundCut cut in regions.Cuts)
        {
            Count(handled++, regions.Cuts.Count);

            List<CurveLoop?> built = [Loop(cut.Outer, z), .. cut.Holes.Select(hole => Loop(hole, z))];
            if (built.Contains(null))
            {
                declined++;
                continue;
            }

            List<CurveLoop> loops = [.. built.OfType<CurveLoop>()];
            try
            {
                FilledRegion region = FilledRegion.Create(_document, RegionType(styleOf(cut.Outer), types), plan.Id, loops);
                drawn++;
                outers.Add(cut.Outer);
                if (!TryStamp(region, stamp))
                {
                    unstamped++;
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                declined++;
            }
        }

        Count(regions.Cuts.Count, regions.Cuts.Count);
        return new DrawnRegions(drawn, declined, unstamped, outers);
    }

    private static string DescribeDrawnPlan(
        string label, string viewName, bool createdView, FootprintExtent? crop, DrawnRegions drawn, int strandedHoles)
    {
        string summary = $"Drew {drawn.Count:N0} {label} region(s) on \"{viewName}\"";
        if (createdView)
        {
            summary += crop is null
                ? ", a new plan left uncropped because this import has no imagery rectangle to crop it to"
                : ", a new plan cropped to the imagery's published rectangle";
        }

        if (drawn.Declined > 0)
        {
            summary += $"; Revit refused {drawn.Declined:N0} region(s) as published, and they were skipped rather than repaired";
        }

        if (strandedHoles > 0)
        {
            summary += $"; {strandedHoles:N0} hole(s) belong to a polygon whose outer ring could not be read";
        }

        if (drawn.Unstamped > 0)
        {
            summary += $"; {drawn.Unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        return summary;
    }

    private ViewPlan? CreateDrawnPlan(string name, FootprintExtent? crop)
    {
        Level? lowest = new FilteredElementCollector(_document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(level => level.Elevation)
            .FirstOrDefault();
        ViewFamilyType? type = new FilteredElementCollector(_document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.FloorPlan);
        if (lowest is null || type is null)
        {
            return null;
        }

        ViewPlan plan = ViewPlan.Create(_document, type.Id, lowest.Id);
        plan.Name = name;
        plan.Scale = HazardPlan.ScaleFor(crop);
        if (crop is { } rectangle)
        {
            SetCrop(plan, rectangle);
        }

        return plan;
    }

    private int DrawKeyRows(
        ViewPlan plan,
        IReadOnlyList<KeyRow> rows,
        string keyStamp,
        FootprintExtent? crop,
        FootprintExtent? drawnExtent,
        double z,
        Dictionary<string, ElementId> types)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        FootprintExtent? existingKey = HazardPlan.Around(RegionsIn(plan)
            .Where(region => string.Equals(CommentsOf(region), keyStamp, StringComparison.Ordinal))
            .Select(region => ExtentOf(region, plan)));

        KeyAnchor anchor = HazardPlan.KeyAnchorFor(crop, drawnExtent, existingKey, plan.Scale);
        IReadOnlyList<KeyRowPlacement> placements = HazardPlan.LayoutKey(anchor, rows.Count, plan.Scale);
        ElementId textType = _document.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);

        List<FootprintExtent?> swatches = [existingKey];
        for (int index = 0; index < rows.Count; index++)
        {
            KeyRowPlacement placement = placements[index];
            if (rows[index].Style is { } style)
            {
                try
                {
                    FilledRegion swatch = FilledRegion.Create(_document, RegionType(style, types), plan.Id, [Rectangle(placement.Swatch, z)]);
                    TryStamp(swatch, keyStamp);
                    swatches.Add(placement.Swatch);
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
                {
                    Trace($"  {plan.Name}: a key swatch for \"{rows[index].Text}\" was refused.");
                }
            }

            if (textType != ElementId.InvalidElementId)
            {
                TextNote.Create(
                    _document,
                    plan.Id,
                    SitePoint(placement.TextEastM, placement.NorthM, z),
                    rows[index].Text,
                    textType);
            }
        }

        if (crop is { } published && plan.CropBoxActive)
        {
            SetCrop(plan, HazardPlan.CropWithKey(published, HazardPlan.Around(swatches)));
        }

        return rows.Count;
    }

    private void SetCrop(ViewPlan plan, FootprintExtent rectangle)
    {
        BoundingBoxXYZ box = plan.CropBox;
        box.Min = SitePoint(rectangle.MinEastM, rectangle.MinNorthM, box.Min.Z);
        box.Max = SitePoint(rectangle.MaxEastM, rectangle.MaxNorthM, box.Max.Z);
        plan.CropBox = box;
        plan.CropBoxActive = true;
        plan.CropBoxVisible = true;
        plan.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(0);
    }

    private ElementId RegionType(RegionStyle style, Dictionary<string, ElementId> cache)
    {
        if (cache.TryGetValue(style.TypeName, out ElementId? known))
        {
            return known;
        }

        FilledRegionType? type = NamedAs<FilledRegionType>(style.TypeName).FirstOrDefault();
        if (type is null)
        {
            FilledRegionType template = new FilteredElementCollector(_document)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .First();
            type = (FilledRegionType)template.Duplicate(style.TypeName);

            ElementId solid = SolidFill();
            ElementId hatch = style.Hatch is null ? ElementId.InvalidElementId : Hatch(style);

            type.IsMasking = false;
            if (style.Fill is { } fill && style.Hatch is { } hatchColour)
            {
                type.BackgroundPatternId = solid;
                type.BackgroundPatternColor = ColourOf(fill);
                type.ForegroundPatternId = hatch;
                type.ForegroundPatternColor = ColourOf(hatchColour);
            }
            else if (style.Fill is { } fillOnly)
            {
                type.ForegroundPatternId = solid;
                type.ForegroundPatternColor = ColourOf(fillOnly);
                type.BackgroundPatternId = ElementId.InvalidElementId;
            }
            else if (style.Hatch is { } hatchOnly)
            {
                type.ForegroundPatternId = hatch;
                type.ForegroundPatternColor = ColourOf(hatchOnly);
                type.BackgroundPatternId = ElementId.InvalidElementId;
            }
        }

        cache[style.TypeName] = type.Id;
        return type.Id;
    }

    private ElementId SolidFill()
        => new FilteredElementCollector(_document)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill)?.Id
            ?? ElementId.InvalidElementId;

    private ElementId Hatch(RegionStyle style)
    {
        if (FillPatternElement.GetFillPatternElementByName(_document, FillPatternTarget.Drafting, style.HatchPatternName) is { } found)
        {
            return found.Id;
        }

        FillPattern pattern = new(
            style.HatchPatternName,
            FillPatternTarget.Drafting,
            FillPatternHostOrientation.ToView,
            style.HatchAngleDeg * Math.PI / 180.0,
            MetresToInternal(HatchSpacingMm / 1000.0));
        return FillPatternElement.Create(_document, pattern).Id;
    }

    private static Color ColourOf(RgbColour colour) => new(colour.Red, colour.Green, colour.Blue);

    private List<FilledRegion> RegionsIn(ViewPlan plan)
        => [.. new FilteredElementCollector(_document, plan.Id).OfClass(typeof(FilledRegion)).Cast<FilledRegion>()];

    private static string? CommentsOf(Element element)
        => element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();

    private static bool TryStamp(Element element, string stamp)
        => element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is { IsReadOnly: false } comments
           && comments.Set(stamp);

    private FootprintExtent? ExtentOf(Element element, View view)
        => element.get_BoundingBox(view) is { } box
            ? new FootprintExtent(
                InternalToMetres(box.Min.X) - Anchoring.OffsetEastM,
                InternalToMetres(box.Min.Y) - Anchoring.OffsetNorthM,
                InternalToMetres(box.Max.X) - Anchoring.OffsetEastM,
                InternalToMetres(box.Max.Y) - Anchoring.OffsetNorthM)
            : null;

    private CurveLoop Rectangle(FootprintExtent extent, double z)
    {
        XYZ a = SitePoint(extent.MinEastM, extent.MinNorthM, z);
        XYZ b = SitePoint(extent.MaxEastM, extent.MinNorthM, z);
        XYZ c = SitePoint(extent.MaxEastM, extent.MaxNorthM, z);
        XYZ d = SitePoint(extent.MinEastM, extent.MaxNorthM, z);
        return CurveLoop.Create([Line.CreateBound(a, b), Line.CreateBound(b, c), Line.CreateBound(c, d), Line.CreateBound(d, a)]);
    }
}
