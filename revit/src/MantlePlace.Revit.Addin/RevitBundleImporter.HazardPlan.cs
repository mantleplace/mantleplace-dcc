using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The hazard steps: the flood zones and the steep ground, drawn as filled regions on a plan of their
// own, with a zone key beside them. What is drawn, where, and whether at all is HazardPlan's.
internal sealed partial class RevitBundleImporter
{
    /// <summary>A drafting hatch's line spacing on paper, in millimetres.</summary>
    private const double HatchSpacingMm = 2.0;

    /// <summary>
    /// One hazard layer on its build's hazard plan: the plan made if it is not there, the layer's
    /// regions drawn and stamped, and its rows added to the zone key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plan view, not the terrain: a flood zone overlaps land cover and roads, and coincident
    /// subdivisions do not draw in cut order. Filled regions persist in the model, sit on a sheet as
    /// they are, and leave the model a visualiser renders untouched — the Analysis Visualization
    /// Framework's results would not survive the project being closed.
    /// </para>
    /// <para>
    /// A region Revit refuses is skipped and counted, never repaired: repairing a published polygon is
    /// derivation. An empty layer draws nothing and makes no plan.
    /// </para>
    /// </remarks>
    private void ImportHazardLayer(ImportStep step, HazardLayer layer)
    {
        if (step.Hazard is not { } facts)
        {
            return;
        }

        bool flood = layer == HazardLayer.FloodZones;
        string label = flood ? "flood zones" : "steep ground";

        if (ReadVectorLayer(step, SiteGeometryKinds.Areas, label) is not { } rings)
        {
            return;
        }

        GroundCutPlan regions = HazardPlan.Regions(rings);
        IReadOnlyList<ZoneKeyRow> rows = flood
            ? [.. ZoneKey.FloodHeading(facts.FloodMap).Select(line => new ZoneKeyRow(line, null)), .. ZoneKey.FloodRows(rings, facts.FloodMap)]
            : ZoneKey.SteepRows(rings, facts.Threshold);
        if (regions.Cuts.Count == 0)
        {
            Say($"The {label} layer ({step.EntryName}) carries no polygon this plugin could draw, so no hazard "
                + "plan was made for it.");
            return;
        }

        string stem = _archive.Layout.Key.Stem;
        string viewName = HazardPlan.ViewName(facts.Build);
        View? existing = NamedAs<View>(viewName).OrderBy(candidate => candidate.IsTemplate).FirstOrDefault();
        HazardViewFound found = existing switch
        {
            null => HazardViewFound.None,
            ViewPlan { IsTemplate: false } => HazardViewFound.PlanView,
            _ => HazardViewFound.SomethingElse,
        };

        List<FilledRegion> onPlan = existing is ViewPlan existingPlan ? RegionsIn(existingPlan) : [];
        HazardPlanDecision decision = HazardPlan.Decide(found, onPlan.Select(CommentsOf), layer, stem, facts.Build);
        if (!decision.Draw)
        {
            Say(decision.Explanation);
            return;
        }

        FootprintExtent? drawnExtent = HazardPlan.Around(regions.Cuts.Select(cut => FootprintExtent.Around(cut.Outer.Vertices)));

        ImportFailureSwallower swallower = new($"Drawing the {label}");
        using Transaction transaction = BeginTransaction($"Mantle Place: {label}", swallower);

        ViewPlan? plan = decision.CreateView ? CreateHazardPlan(viewName, facts.Crop ?? drawnExtent) : existing as ViewPlan;
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

        int drawn = 0;
        int declined = 0;
        int unstamped = 0;
        foreach (GroundCut cut in regions.Cuts)
        {
            if (Loop(cut.Outer, z) is not { } outer)
            {
                declined++;
                continue;
            }

            List<CurveLoop> loops = [outer, .. cut.Holes.Select(hole => Loop(hole, z)).OfType<CurveLoop>()];
            HazardStyle style = flood
                ? HazardStyles.ForFloodZone(cut.Outer.FloodZone, cut.Outer.FloodZoneSubtype)
                : HazardStyles.SteepGround;

            try
            {
                FilledRegion region = FilledRegion.Create(_document, RegionType(style, types), plan.Id, loops);
                drawn++;
                if (!TryStamp(region, stamp))
                {
                    unstamped++;
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                // A ring that touches itself is common in outlines traced from a raster. It is one
                // region lost, counted and said — never repaired into a shape nobody published.
                declined++;
            }
        }

        int keyRows = DrawKeyRows(plan, rows, stem, facts, drawnExtent, z, types);

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

        Say(summary + $". The zone key gained {keyRows:N0} row(s). These are context, not a determination.");
    }

    /// <summary>
    /// A floor plan on the lowest level, named for its build, at a scale that fits the site, cropped
    /// to <paramref name="crop"/> when there is one — or <c>null</c> when the project has no level.
    /// </summary>
    /// <remarks>
    /// The lowest level because an import that leaves the terrain out makes no level choice of its
    /// own, and a plan cannot borrow one that was never made. The filled regions belong to the view,
    /// so the level sets only the plan's name in the browser and its view range.
    /// </remarks>
    private ViewPlan? CreateHazardPlan(string name, FootprintExtent? crop)
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

    /// <summary>The key's rows for this layer, added below any the plan already holds.</summary>
    /// <returns>How many rows were added.</returns>
    private int DrawKeyRows(
        ViewPlan plan,
        IReadOnlyList<ZoneKeyRow> rows,
        string stem,
        HazardPlanFacts facts,
        FootprintExtent? drawnExtent,
        double z,
        Dictionary<string, ElementId> types)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        string keyStamp = HazardPlan.KeyStamp(stem, facts.Build);
        FootprintExtent? existingKey = HazardPlan.Around(RegionsIn(plan)
            .Where(region => string.Equals(CommentsOf(region), keyStamp, StringComparison.Ordinal))
            .Select(region => ExtentOf(region, plan)));

        KeyAnchor anchor = HazardPlan.KeyAnchorFor(facts.Crop, drawnExtent, existingKey, plan.Scale);
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
                    Trace($"  hazard plan: a key swatch for \"{rows[index].Text}\" was refused.");
                }
            }

            if (textType != ElementId.InvalidElementId)
            {
                TextNote.Create(
                    _document,
                    plan.Id,
                    new XYZ(MetresToInternal(placement.TextEastM), MetresToInternal(placement.NorthM), z),
                    rows[index].Text,
                    textType);
            }
        }

        // The crop takes the swatches in, or the crop region would hide the key that explains it.
        if (facts.Crop is { } crop && plan.CropBoxActive)
        {
            SetCrop(plan, HazardPlan.CropWithKey(crop, HazardPlan.Around(swatches)));
        }

        return rows.Count;
    }

    /// <summary>Sets a plan's crop to a rectangle in frame metres, with the annotation crop off.</summary>
    /// <remarks>
    /// Annotation crop off, so the key's text — annotation — shows beside the swatches the model crop
    /// takes in. A plan's crop box is in model coordinates in plan, so the rectangle goes in as it is.
    /// </remarks>
    private static void SetCrop(ViewPlan plan, FootprintExtent rectangle)
    {
        BoundingBoxXYZ box = plan.CropBox;
        box.Min = new XYZ(MetresToInternal(rectangle.MinEastM), MetresToInternal(rectangle.MinNorthM), box.Min.Z);
        box.Max = new XYZ(MetresToInternal(rectangle.MaxEastM), MetresToInternal(rectangle.MaxNorthM), box.Max.Z);
        plan.CropBox = box;
        plan.CropBoxActive = true;
        plan.CropBoxVisible = true;
        plan.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(0);
    }

    /// <summary>
    /// The filled region type a style names, found by name or made from the project's first — so a
    /// curator who recoloured one keeps their colour on the next import.
    /// </summary>
    private ElementId RegionType(HazardStyle style, Dictionary<string, ElementId> cache)
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

    /// <summary>The project's solid fill pattern.</summary>
    private ElementId SolidFill()
        => new FilteredElementCollector(_document)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill)?.Id
            ?? ElementId.InvalidElementId;

    /// <summary>A style's drafting hatch, found by name or made.</summary>
    private ElementId Hatch(HazardStyle style)
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

    private static FootprintExtent? ExtentOf(Element element, View view)
        => element.get_BoundingBox(view) is { } box
            ? new FootprintExtent(
                InternalToMetres(box.Min.X),
                InternalToMetres(box.Min.Y),
                InternalToMetres(box.Max.X),
                InternalToMetres(box.Max.Y))
            : null;

    private static CurveLoop Rectangle(FootprintExtent extent, double z)
    {
        XYZ a = new(MetresToInternal(extent.MinEastM), MetresToInternal(extent.MinNorthM), z);
        XYZ b = new(MetresToInternal(extent.MaxEastM), MetresToInternal(extent.MinNorthM), z);
        XYZ c = new(MetresToInternal(extent.MaxEastM), MetresToInternal(extent.MaxNorthM), z);
        XYZ d = new(MetresToInternal(extent.MinEastM), MetresToInternal(extent.MaxNorthM), z);
        return CurveLoop.Create([Line.CreateBound(a, b), Line.CreateBound(b, c), Line.CreateBound(c, d), Line.CreateBound(d, a)]);
    }
}
