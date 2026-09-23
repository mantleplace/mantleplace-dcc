using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Places one Planting family instance at a published tree point — <see cref="TreeFamily"/> or
/// <see cref="ShrubFamily"/>, by its foliage type — and reads what a loaded family offers. Shared by
/// the planting step and the developer command that authors the families, so the instance the
/// command measures is placed by the same code an import runs.
/// </summary>
internal static class TreeInstances
{
    /// <summary>
    /// Creates the instance on <paramref name="level"/>, lifts it to the tree's ground, and writes the
    /// published height and crown radius to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ground elevation goes into the instance's offset from its level rather than into the
    /// location's Z, because a level-based family takes its elevation from the level and reads the
    /// location's Z only as a hint. Written explicitly, the tree stands where the file says whatever
    /// Revit makes of the hint.
    /// </para>
    /// <para>
    /// Returns the instance even when a parameter would not take its value, with
    /// <paramref name="sized"/> false: the tree is real, and the step counts it as one a curator
    /// should know stands at the family's default size or at its level's elevation.
    /// </para>
    /// </remarks>
    internal static FamilyInstance Place(Document document, FamilySymbol symbol, Level level, SiteTreePoint tree, out bool sized)
    {
        double ground = MetresToInternal(tree.GroundElevationM);
        XYZ location = new(MetresToInternal(tree.EastM), MetresToInternal(tree.NorthM), ground);

        FamilyInstance instance = document.Create.NewFamilyInstance(location, symbol, level, StructuralType.NonStructural);

        Parameter? offset = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
        bool lifted = offset is { IsReadOnly: false } && offset.Set(ground - level.ProjectElevation);

        // Every write is attempted whatever the one before it did: a tree at the wrong elevation is
        // still better at its published size, and the other way round.
        bool tall = SetLength(instance, PlantingFamilies.HeightParameter(tree.FoliageType), tree.HeightM);
        bool wide = SetLength(instance, TreeFamily.CrownRadiusParameter, tree.CrownRadiusM);
        sized = lifted && tall && wide;
        return instance;
    }

    /// <summary>The family's parameters, as <see cref="TreeFamilyChoice"/> needs to see them.</summary>
    /// <remarks>
    /// A project only shows a family's type parameters, on its symbols; whether a parameter is per
    /// instance is known only to the family document. So this opens it, reads it and closes it
    /// unsaved — once per import, outside any transaction, which <c>EditFamily</c> requires.
    /// </remarks>
    internal static List<TreeFamilyParameter> ParametersOf(Document document, Family family)
    {
        Document definition = document.EditFamily(family);
        try
        {
            return [.. definition.FamilyManager.GetParameters()
                .Select(parameter => new TreeFamilyParameter(parameter.Definition.Name, parameter.IsInstance))];
        }
        finally
        {
            definition.Close(false);
        }
    }

    /// <summary>The family already in the project under <paramref name="familyName"/>, if any.</summary>
    internal static Family? Find(Document document, string familyName)
    {
        using FilteredElementCollector collector = new(document);
        return collector
            .OfClass(typeof(Family))
            .Cast<Family>()
            .FirstOrDefault(family => string.Equals(family.Name, familyName, StringComparison.Ordinal));
    }

    private static bool SetLength(FamilyInstance instance, string name, double metres)
    {
        Parameter? parameter = instance.LookupParameter(name);
        return parameter is { IsReadOnly: false, StorageType: StorageType.Double }
            && parameter.Set(MetresToInternal(metres));
    }

    internal static double MetresToInternal(double metres)
        => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);
}
