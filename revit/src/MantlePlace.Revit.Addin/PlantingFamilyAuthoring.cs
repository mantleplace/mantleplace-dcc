using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using MantlePlace.Core;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace MantlePlace.Revit.Addin;

internal static class PlantingFamilyAuthoring
{
    internal const string DirectoryVariable = "MANTLEPLACE_AUTHOR_PLANTING_FAMILIES";

    private const string TemplateFileName = "Metric Planting.rft";

    private const string AuthoringRevit = "2025";

    private const string TrunkHeightParameter = "Trunk Height";
    private const string TrunkRadiusParameter = "Trunk Radius";
    private const string CrownApexRadiusParameter = "Crown Apex Radius";

    private const string WaistHeightParameter = "Waist Height";
    private const string BaseRadiusParameter = "Base Radius";
    private const string ApexRadiusParameter = "Apex Radius";

    private const double ToleranceM = 0.005;

    private sealed record FamilySpec(
        FoliageType Foliage,
        string FamilyName,
        string FileName,
        string HeightParameter,
        double DefaultHeightM,
        double DefaultCrownRadiusM,
        (double HeightM, double CrownRadiusM)[] Flexes,
        SiteTreePoint Probe)
    {
        internal string LogFileName => FamilyName + ".authoring.log";
    }

    private static readonly FamilySpec Tree = new(
        FoliageType.Tree,
        TreeFamily.FamilyName,
        TreeFamily.FileName,
        TreeFamily.HeightParameter,
        DefaultHeightM: 10.0,
        DefaultCrownRadiusM: 3.0,
        [(12.0, 3.0), (4.0, 1.25), (10.0, 3.0)],
        new SiteTreePoint(12.0, -7.5, 104.25, 13.5, 2.75, FoliageType.Tree));

    private static readonly FamilySpec Shrub = new(
        FoliageType.Shrub,
        ShrubFamily.FamilyName,
        ShrubFamily.FileName,
        ShrubFamily.HeightParameter,
        DefaultHeightM: 1.5,
        DefaultCrownRadiusM: 1.0,
        [(2.5, 1.0), (0.8, 0.45), (1.5, 1.0)],
        new SiteTreePoint(3.0, -2.25, 104.25, 1.8, 1.1, FoliageType.Shrub));

    internal static bool Run(RevitApplication application, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(application);

        StringBuilder treeLog = new();
        StringBuilder shrubLog = new();
        bool treeAgreed = AuthorOne(application, outputDirectory, Tree, treeLog);
        bool shrubAgreed = AuthorOne(application, outputDirectory, Shrub, shrubLog);
        bool agreed = treeAgreed && shrubAgreed;

        string verdict = agreed
            ? "RUN: both families agreed. Both files are ready to commit."
            : $"RUN: NOT READY. {(treeAgreed ? string.Empty : $"\"{Tree.FamilyName}\" did not agree. ")}"
                + $"{(shrubAgreed ? string.Empty : $"\"{Shrub.FamilyName}\" did not agree. ")}"
                + "Commit neither family from this run.";

        bool written = Write(outputDirectory, Tree, treeLog, verdict) & Write(outputDirectory, Shrub, shrubLog, verdict);
        return agreed && written;
    }

    private static bool Write(string outputDirectory, FamilySpec spec, StringBuilder log, string verdict)
    {
        log.AppendLine(verdict);
        try
        {
            File.WriteAllText(Path.Combine(outputDirectory, spec.LogFileName), log.ToString());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool AuthorOne(RevitApplication application, string outputDirectory, FamilySpec spec, StringBuilder log)
    {
        string familyPath = Path.Combine(outputDirectory, spec.FileName);
        bool agreed = false;

        try
        {
            Directory.CreateDirectory(outputDirectory);
            log.AppendLine(CultureInfo.InvariantCulture,
                $"Mantle Place {PluginVersion.Current} \"{spec.FamilyName}\" authoring, Revit {application.VersionNumber} ({application.VersionBuild}).");

            string template = FindTemplate(application);
            log.AppendLine(CultureInfo.InvariantCulture, $"Template: {template}");

            Document family = application.NewFamilyDocument(template);
            try
            {
                Describe(family, "Template as opened", log);
                if (spec.Foliage == FoliageType.Shrub)
                {
                    AuthorShrub(family, spec, log);
                }
                else
                {
                    AuthorTree(family, spec, log);
                }

                agreed = true;
                foreach ((double heightM, double crownRadiusM) in spec.Flexes)
                {
                    agreed &= Flex(family, spec, heightM, crownRadiusM, log);
                }

                Purge(family, spec, log);
                Describe(family, "Family as saved", log);

                if (File.Exists(familyPath))
                {
                    File.Delete(familyPath);
                }

                family.SaveAs(familyPath, new SaveAsOptions { OverwriteExistingFile = true, Compact = true, MaximumBackups = 1 });
            }
            finally
            {
                family.Close(false);
            }

            log.AppendLine(CultureInfo.InvariantCulture,
                $"Saved {familyPath} ({new FileInfo(familyPath).Length:N0} bytes).");

            agreed &= RecordsNoFolder(application, familyPath, log);
            agreed &= SavedByTheFloor(application, log);
            agreed &= VerifyPlacement(application, familyPath, spec, log);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException
                                       or IOException)
        {
            agreed = false;
            log.AppendLine(CultureInfo.InvariantCulture, $"ABORTED: {ex.GetType().Name}: {ex.Message}");
        }

        log.AppendLine(agreed
            ? "RESULT: every measurement of this family agreed."
            : "RESULT: NOT READY. Something above did not agree; do not commit this family.");
        return agreed;
    }

    private static bool RecordsNoFolder(RevitApplication application, string familyPath, StringBuilder log)
    {
        string savedTo = Path.GetFullPath(familyPath);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool neutral = !savedTo.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            && !savedTo.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase);

        log.AppendLine(CultureInfo.InvariantCulture,
            $"Recorded in the file: saved to \"{savedTo}\" ({(neutral ? "neutral" : "NAMES THIS MACHINE'S USER")}), "
            + $"by Revit user \"{application.Username}\" (check it may be public before committing).");
        return neutral;
    }

    private static bool SavedByTheFloor(RevitApplication application, StringBuilder log)
    {
        bool floor = string.Equals(application.VersionNumber, AuthoringRevit, StringComparison.Ordinal);
        if (!floor)
        {
            log.AppendLine(CultureInfo.InvariantCulture,
                $"Saved by Revit {application.VersionNumber}: Revit {AuthoringRevit} cannot load it. Run this in Revit {AuthoringRevit}.");
        }

        return floor;
    }

    private static string FindTemplate(RevitApplication application)
    {
        string root = application.FamilyTemplatePath;
        string[] candidates = Directory.Exists(root)
            ? Directory.GetFiles(root, TemplateFileName, SearchOption.AllDirectories)
            : [];

        return candidates.Length > 0
            ? candidates[0]
            : throw new InvalidOperationException(
                $"No \"{TemplateFileName}\" under the family template path \"{root}\". Install Revit's "
                + "English family templates, or point Options ▸ File Locations at them.");
    }

    private static void AuthorTree(Document family, FamilySpec spec, StringBuilder log)
    {
        FamilyManager manager = family.FamilyManager;
        using Transaction transaction = new(family, "Author the Mantle Place Tree");
        transaction.Start();

        (FamilyParameter height, FamilyParameter crown) = Driving(manager, spec, log);

        FamilyParameter trunkHeight = InstanceLength(manager, TrunkHeightParameter, log);
        FamilyParameter trunkRadius = InstanceLength(manager, TrunkRadiusParameter, log);
        FamilyParameter apexRadius = InstanceLength(manager, CrownApexRadiusParameter, log);
        manager.SetFormula(trunkHeight, TreeFamily.TrunkHeightFormula);
        manager.SetFormula(trunkRadius, TreeFamily.TrunkRadiusFormula);
        manager.SetFormula(apexRadius, TreeFamily.CrownApexRadiusFormula);

        (SketchPlane plane, ViewPlan plan) = Canvas(family);

        double defaultTrunkHeight = Internal(spec.DefaultHeightM * TreeFamily.TrunkHeightFraction);

        Extrusion trunk = family.FamilyCreate.NewExtrusion(
            true,
            Profile(Circle(0.0, Internal(spec.DefaultCrownRadiusM * TreeFamily.TrunkRadiusFraction))),
            plane,
            defaultTrunkHeight);
        manager.AssociateElementParameterToFamilyParameter(trunk.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), trunkHeight);
        Label(family, plan, trunk.Sketch, trunkRadius, log, "trunk");

        Blend crownForm = family.FamilyCreate.NewBlend(
            true,
            Circle(Internal(spec.DefaultHeightM), Internal(spec.DefaultCrownRadiusM * TreeFamily.CrownApexFraction)),
            Circle(0.0, Internal(spec.DefaultCrownRadiusM)),
            plane);
        manager.AssociateElementParameterToFamilyParameter(crownForm.get_Parameter(BuiltInParameter.BLEND_START_PARAM), trunkHeight);
        manager.AssociateElementParameterToFamilyParameter(crownForm.get_Parameter(BuiltInParameter.BLEND_END_PARAM), height);
        Label(family, plan, crownForm.BottomSketch, crown, log, "crown base");
        Label(family, plan, crownForm.TopSketch, apexRadius, log, "crown apex");

        Paint(family, trunk, "Mantle Place Tree Trunk", new Color(104, 82, 60));
        Paint(family, crownForm, "Mantle Place Tree Crown", new Color(84, 122, 68));

        transaction.Commit();
        log.AppendLine(CultureInfo.InvariantCulture,
            $"Authored: trunk extrusion {trunk.Id}, crown blend {crownForm.Id}.");
    }

    private static void AuthorShrub(Document family, FamilySpec spec, StringBuilder log)
    {
        FamilyManager manager = family.FamilyManager;
        using Transaction transaction = new(family, "Author the Mantle Place Shrub");
        transaction.Start();

        (FamilyParameter height, FamilyParameter crown) = Driving(manager, spec, log);

        FamilyParameter waistHeight = InstanceLength(manager, WaistHeightParameter, log);
        FamilyParameter baseRadius = InstanceLength(manager, BaseRadiusParameter, log);
        FamilyParameter apexRadius = InstanceLength(manager, ApexRadiusParameter, log);
        manager.SetFormula(waistHeight, ShrubFamily.WaistHeightFormula);
        manager.SetFormula(baseRadius, ShrubFamily.BaseRadiusFormula);
        manager.SetFormula(apexRadius, ShrubFamily.ApexRadiusFormula);

        (SketchPlane plane, ViewPlan plan) = Canvas(family);

        double defaultWaist = Internal(spec.DefaultHeightM * ShrubFamily.WaistHeightFraction);
        double defaultCrown = Internal(spec.DefaultCrownRadiusM);

        Blend lower = family.FamilyCreate.NewBlend(
            true,
            Circle(defaultWaist, defaultCrown),
            Circle(0.0, Internal(spec.DefaultCrownRadiusM * ShrubFamily.BaseRadiusFraction)),
            plane);
        manager.AssociateElementParameterToFamilyParameter(lower.get_Parameter(BuiltInParameter.BLEND_END_PARAM), waistHeight);
        Label(family, plan, lower.BottomSketch, baseRadius, log, "dome base");
        Label(family, plan, lower.TopSketch, crown, log, "dome waist, from below");

        Blend upper = family.FamilyCreate.NewBlend(
            true,
            Circle(Internal(spec.DefaultHeightM), Internal(spec.DefaultCrownRadiusM * ShrubFamily.ApexRadiusFraction)),
            Circle(0.0, defaultCrown),
            plane);
        manager.AssociateElementParameterToFamilyParameter(upper.get_Parameter(BuiltInParameter.BLEND_START_PARAM), waistHeight);
        manager.AssociateElementParameterToFamilyParameter(upper.get_Parameter(BuiltInParameter.BLEND_END_PARAM), height);
        Label(family, plan, upper.BottomSketch, crown, log, "dome waist, from above");
        Label(family, plan, upper.TopSketch, apexRadius, log, "dome apex");

        Color foliage = new(142, 168, 72);
        Paint(family, lower, "Mantle Place Shrub Foliage", foliage);
        Paint(family, upper, "Mantle Place Shrub Foliage", foliage);

        transaction.Commit();
        log.AppendLine(CultureInfo.InvariantCulture,
            $"Authored: lower blend {lower.Id}, upper blend {upper.Id}.");
    }

    private static (FamilyParameter Height, FamilyParameter Crown) Driving(FamilyManager manager, FamilySpec spec, StringBuilder log)
    {
        if (manager.CurrentType is null)
        {
            manager.NewType(spec.FamilyName);
        }

        FamilyParameter height = InstanceLength(manager, spec.HeightParameter, log);
        FamilyParameter crown = InstanceLength(manager, TreeFamily.CrownRadiusParameter, log);
        manager.Set(height, Internal(spec.DefaultHeightM));
        manager.Set(crown, Internal(spec.DefaultCrownRadiusM));
        return (height, crown);
    }

    private static (SketchPlane Plane, ViewPlan Plan) Canvas(Document family)
    {
        Level level = new FilteredElementCollector(family).OfClass(typeof(Level)).Cast<Level>().First();
        SketchPlane plane = SketchPlane.Create(family, level.Id);
        ViewPlan plan = new FilteredElementCollector(family)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .First(view => !view.IsTemplate && view.GenLevel?.Id == level.Id);
        return (plane, plan);
    }

    private static FamilyParameter InstanceLength(FamilyManager manager, string name, StringBuilder log)
    {
        if (manager.get_Parameter(name) is { } existing)
        {
            log.AppendLine(CultureInfo.InvariantCulture,
                $"  \"{name}\" already exists ({(existing.IsInstance ? "instance" : "type")}, "
                + $"{Kind(existing)}); making it an instance parameter.");
            if (!existing.IsInstance)
            {
                manager.MakeInstance(existing);
            }

            return existing;
        }

        return manager.AddParameter(name, GroupTypeId.Geometry, SpecTypeId.Length, isInstance: true);
    }

    private static void Label(Document family, View plan, Sketch sketch, FamilyParameter radius, StringBuilder log, string what)
    {
        int labelled = 0;
        foreach (ElementId id in sketch.GetAllElements())
        {
            if (family.GetElement(id) is not CurveElement { GeometryCurve: Arc arc } element)
            {
                continue;
            }

            XYZ leader = arc.Center + (arc.Radius * 0.5 * XYZ.BasisX);
            Dimension dimension = family.FamilyCreate.NewRadialDimension(plan, element.GeometryCurve.Reference, leader);
            dimension.FamilyLabel = radius;
            labelled++;
        }

        log.AppendLine(CultureInfo.InvariantCulture, $"  Labelled {labelled} arc(s) of the {what} with \"{radius.Definition.Name}\".");
        if (labelled == 0)
        {
            throw new InvalidOperationException($"The {what} sketch has no arc to label.");
        }
    }

    private static void Paint(Document family, GenericForm form, string name, Color color)
    {
        ElementId material = new FilteredElementCollector(family)
            .OfClass(typeof(Material))
            .FirstOrDefault(element => element.Name == name)?.Id
            ?? Material.Create(family, name);
        ((Material)family.GetElement(material)).Color = color;
        form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(material);
    }

    private static bool Flex(Document family, FamilySpec spec, double heightM, double crownRadiusM, StringBuilder log)
    {
        FamilyManager manager = family.FamilyManager;
        using Transaction transaction = new(family, "Flex " + spec.FamilyName);
        transaction.Start();
        manager.Set(manager.get_Parameter(spec.HeightParameter), Internal(heightM));
        manager.Set(manager.get_Parameter(TreeFamily.CrownRadiusParameter), Internal(crownRadiusM));
        family.Regenerate();

        BoundingBoxXYZ? box = null;
        foreach (GenericForm form in new FilteredElementCollector(family).OfClass(typeof(GenericForm)).Cast<GenericForm>())
        {
            box = Union(box, form.get_BoundingBox(null));
        }

        transaction.Commit();

        return Agrees($"Flex to height {heightM} m, crown {crownRadiusM} m", box, 0.0, heightM, crownRadiusM, log);
    }

    private static bool VerifyPlacement(RevitApplication application, string familyPath, FamilySpec spec, StringBuilder log)
    {
        Document project = application.NewProjectDocument(Autodesk.Revit.DB.UnitSystem.Metric);
        try
        {
            using Transaction transaction = new(project, "Place " + spec.FamilyName);
            transaction.Start();

            if (!project.LoadFamily(familyPath, out Family family))
            {
                log.AppendLine("Placement: the saved family did not load into a scratch project.");
                return false;
            }

            log.AppendLine(CultureInfo.InvariantCulture, $"Placement: loaded as \"{family.Name}\".");
            FamilySymbol symbol = (FamilySymbol)project.GetElement(family.GetFamilySymbolIds().First());
            symbol.Activate();

            Level level = new FilteredElementCollector(project).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(candidate => candidate.ProjectElevation).First();

            SiteTreePoint point = spec.Probe;
            FamilyInstance instance = TreeInstances.Place(project, symbol, level, point, out bool sized);
            project.Regenerate();

            log.AppendLine(CultureInfo.InvariantCulture,
                $"Placement: category {instance.Category?.Name}, sized {sized}, level \"{level.Name}\" at "
                + $"{Metres(level.ProjectElevation)} m.");
            bool agreed = sized & Agrees(
                "Placed instance",
                instance.get_BoundingBox(null),
                point.GroundElevationM,
                point.HeightM,
                point.CrownRadiusM,
                log,
                point.EastM,
                point.NorthM);

            transaction.RollBack();
            return agreed;
        }
        finally
        {
            project.Close(false);
        }
    }

    private static bool Agrees(
        string what,
        BoundingBoxXYZ? box,
        double groundM,
        double heightM,
        double crownRadiusM,
        StringBuilder log,
        double eastM = 0.0,
        double northM = 0.0)
    {
        if (box is null)
        {
            log.AppendLine(CultureInfo.InvariantCulture, $"{what}: NO GEOMETRY.");
            return false;
        }

        double[] measured =
        [
            Metres(box.Min.Z), Metres(box.Max.Z),
            Metres(box.Min.X), Metres(box.Max.X),
            Metres(box.Min.Y), Metres(box.Max.Y),
        ];
        double[] expected =
        [
            groundM, groundM + heightM,
            eastM - crownRadiusM, eastM + crownRadiusM,
            northM - crownRadiusM, northM + crownRadiusM,
        ];

        bool agreed = measured.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= ToleranceM);
        log.AppendLine(CultureInfo.InvariantCulture,
            $"{what}: {(agreed ? "agrees" : "DISAGREES")} — z {measured[0]:0.000}..{measured[1]:0.000} "
            + $"(want {expected[0]:0.000}..{expected[1]:0.000}), x {measured[2]:0.000}..{measured[3]:0.000} "
            + $"(want {expected[2]:0.000}..{expected[3]:0.000}), y {measured[4]:0.000}..{measured[5]:0.000} "
            + $"(want {expected[4]:0.000}..{expected[5]:0.000}).");
        return agreed;
    }

    private static void Purge(Document family, FamilySpec spec, StringBuilder log)
    {
        int purged = 0;
        for (int pass = 0; pass < 5; pass++)
        {
            ISet<ElementId> unused = family.GetUnusedElements(new HashSet<ElementId>());
            if (unused.Count == 0)
            {
                break;
            }

            using Transaction transaction = new(family, "Purge " + spec.FamilyName);
            transaction.Start();
            int before = purged;
            foreach (ElementId id in unused)
            {
                try
                {
                    purged += family.Delete(id).Count > 0 ? 1 : 0;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                }
            }

            transaction.Commit();
            if (purged == before)
            {
                break;
            }
        }

        log.AppendLine(CultureInfo.InvariantCulture, $"Purged {purged} unused element(s).");
    }

    private static void Describe(Document family, string heading, StringBuilder log)
    {
        FamilyManager manager = family.FamilyManager;
        log.AppendLine(CultureInfo.InvariantCulture,
            $"{heading}: category {family.OwnerFamily.FamilyCategory?.Name}, {manager.Types.Size} type(s), "
            + $"current \"{manager.CurrentType?.Name}\".");
        foreach (FamilyParameter parameter in manager.GetParameters())
        {
            log.AppendLine(CultureInfo.InvariantCulture,
                $"  {parameter.Definition.Name}: {(parameter.IsInstance ? "instance" : "type")}, {Kind(parameter)}{(parameter.IsDeterminedByFormula ? ", = " + parameter.Formula : string.Empty)}");
        }
    }

    private static string Kind(FamilyParameter parameter)
        => parameter.Definition is InternalDefinition { BuiltInParameter: not BuiltInParameter.INVALID } builtIn
            ? $"built-in {builtIn.BuiltInParameter}"
            : "family";

    private static CurveArrArray Profile(CurveArray loop)
    {
        CurveArrArray profile = new();
        profile.Append(loop);
        return profile;
    }

    private static CurveArray Circle(double z, double radius)
    {
        XYZ centre = new(0.0, 0.0, z);
        CurveArray loop = new();
        loop.Append(Arc.Create(centre, radius, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY));
        loop.Append(Arc.Create(centre, radius, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY));
        return loop;
    }

    private static BoundingBoxXYZ? Union(BoundingBoxXYZ? a, BoundingBoxXYZ? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        return new BoundingBoxXYZ
        {
            Min = new XYZ(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
            Max = new XYZ(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z)),
        };
    }

    private static double Internal(double metres) => TreeInstances.MetresToInternal(metres);

    private static double Metres(double internalUnits) => UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Meters);
}
