// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The developer command that authors <c>Mantle Place Tree.rfa</c> and <c>Mantle Place Shrub.rfa</c>
/// from the installed Revit's Planting template, measures them, and saves them for committing. Not
/// product: no button, no curator ever reaches it.
/// </summary>
/// <remarks>
/// <para>
/// Set <see cref="DirectoryVariable"/> to a folder and start Revit <b>2025</b>; once Revit has
/// initialised, this writes both families and one log per family into that folder and changes
/// nothing else. 2025 because a family saved by a Revit loads in that Revit and every later one,
/// never an earlier one — the same floor the shim compiles against. The files it writes are committed
/// as <c>Families/MantlePlaceTree.rfa</c> and <c>Families/MantlePlaceShrub.rfa</c> beside this source
/// and embedded in the assembly; see <c>revit/README.md</c> for the whole loop.
/// </para>
/// <para>
/// It is code rather than a click-by-click recipe so that each family is a function of this file:
/// its parameters, formulas and proportions are <see cref="TreeFamily"/>'s or
/// <see cref="ShrubFamily"/>'s, the same constants the DirectShape fallback builds with, and a change
/// to them is a re-run rather than a re-draw.
/// </para>
/// <para>
/// ⛔ <b>It is not finished until both logs say the measurements agree.</b> For each family, three
/// flexes in the family document and one placed instance in a scratch project are measured against
/// the numbers that drove them. A family whose dimension labels did not take would still save, still
/// load and still place — every point at the template's default size — so the measurement is the
/// only proof. One run writes both families, and neither is ready to commit unless both agreed:
/// each log ends with the run's verdict as well as its own.
/// </para>
/// </remarks>
internal static class PlantingFamilyAuthoring
{
    /// <summary>The folder the families and their logs are written to. Unset, nothing happens.</summary>
    internal const string DirectoryVariable = "MANTLEPLACE_AUTHOR_PLANTING_FAMILIES";

    private const string TemplateFileName = "Metric Planting.rft";

    /// <summary>The only Revit whose saved family every supported Revit can load.</summary>
    private const string AuthoringRevit = "2025";

    private const string TrunkHeightParameter = "Trunk Height";
    private const string TrunkRadiusParameter = "Trunk Radius";
    private const string CrownApexRadiusParameter = "Crown Apex Radius";

    private const string WaistHeightParameter = "Waist Height";
    private const string BaseRadiusParameter = "Base Radius";
    private const string ApexRadiusParameter = "Apex Radius";

    /// <summary>How closely a measured extent must agree with the number that drove it.</summary>
    private const double ToleranceM = 0.005;

    /// <summary>
    /// One family to author: what it is called, its defaults — a plant a curator would recognise if
    /// they placed one by hand — the sizes it is flexed through, and the point it is placed at.
    /// </summary>
    /// <param name="Flexes">
    /// Height and crown pairs. The first two move height and crown in different directions, so a label
    /// tied to the wrong parameter shows; the last is the default, which the type saves with.
    /// </param>
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

    /// <summary>
    /// Authors, measures and saves both families. Always writes both logs, whatever happened.
    /// </summary>
    /// <returns>Whether both families were saved and every measurement of both agreed.</returns>
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
            // Nowhere left to say it; the handler this runs in must not take Revit's startup down.
            return false;
        }
    }

    /// <summary>Authors, measures and saves one family, and says in its log whether it agreed.</summary>
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

                // `&`, not `&&`: every flex runs and is logged even after one disagrees.
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

    /// <summary>
    /// Whether the saved file names the folder it was made in. It is committed to a public repository.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Revit writes the full path a file was saved to into the file, where the Family Editor never
    /// shows it and where it is permanent once pushed. A family saved under a user profile carries
    /// that user's login name and whatever else the path says about the machine, so the command
    /// refuses to call it ready: point the variable at a neutral folder such as
    /// <c>C:\MantlePlace</c>.
    /// </para>
    /// <para>
    /// Revit also writes the saving Revit's user name, which for a Revit signed in to an Autodesk
    /// account is that account's name and cannot be changed from here. It is reported, not refused:
    /// whoever commits the file decides whether that name may be public.
    /// </para>
    /// </remarks>
    private static bool RecordsNoFolder(RevitApplication application, string familyPath, StringBuilder log)
    {
        // The path Revit records is the one it was just saved to.
        string savedTo = Path.GetFullPath(familyPath);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool neutral = !savedTo.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            && !savedTo.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase);

        log.AppendLine(CultureInfo.InvariantCulture,
            $"Recorded in the file: saved to \"{savedTo}\" ({(neutral ? "neutral" : "NAMES THIS MACHINE'S USER")}), "
            + $"by Revit user \"{application.Username}\" (check it may be public before committing).");
        return neutral;
    }

    /// <summary>Whether this Revit is the one every supported Revit can load a family from.</summary>
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

        // FamilyTemplatePath names the install's language folder, where the template is directly.
        // Searching below it tolerates an install that points one level up.
        return candidates.Length > 0
            ? candidates[0]
            : throw new InvalidOperationException(
                $"No \"{TemplateFileName}\" under the family template path \"{root}\". Install Revit's "
                + "English family templates, or point Options ▸ File Locations at them.");
    }

    /// <summary>
    /// Adds the tree's parameters, draws a trunk and a crown, and ties every dimension of both to them.
    /// </summary>
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

    /// <summary>
    /// Adds the shrub's parameters, draws its dome as two stacked blends, and ties every dimension of
    /// both to them.
    /// </summary>
    /// <remarks>
    /// Two blends meeting at the widest circle, not a revolve: <see cref="Label"/> drives a size by a
    /// radial dimension on every arc of a sketch, which a circle has and a revolve's profile does not.
    /// </remarks>
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

        // Yellower and lighter than the tree's crown, so the two read apart in a shaded view.
        Color foliage = new(142, 168, 72);
        Paint(family, lower, "Mantle Place Shrub Foliage", foliage);
        Paint(family, upper, "Mantle Place Shrub Foliage", foliage);

        transaction.Commit();
        log.AppendLine(CultureInfo.InvariantCulture,
            $"Authored: lower blend {lower.Id}, upper blend {upper.Id}.");
    }

    /// <summary>The type, and the two instance parameters the planting step writes, at their defaults.</summary>
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

    /// <summary>The sketch plane forms are drawn on, and the plan their radii are dimensioned in.</summary>
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

    /// <summary>An instance length parameter, created unless the template already has one of that name.</summary>
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

    /// <summary>Labels every arc in a sketch with a radius parameter.</summary>
    /// <remarks>
    /// A circle in a sketch is two arcs, and each carries its own radius. Labelling one leaves the
    /// other at its drawn size, and the loop tears the first time the label changes.
    /// </remarks>
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

    /// <summary>Drives the family to a size, regenerates, and measures the result against it.</summary>
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

    /// <summary>
    /// Loads the saved family into a scratch project, places one point through
    /// <see cref="TreeInstances"/> — the planting step's own code — and measures it.
    /// </summary>
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

    /// <summary>Deletes what the template brought and the family does not use, so the file is small.</summary>
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

    /// <summary>A horizontal circle about the family origin as two half-arcs.</summary>
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
