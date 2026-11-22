using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal static class TreeInstances
{
    internal static FamilyInstance Place(
        Document document,
        FamilySymbol symbol,
        Level level,
        SiteTreePoint tree,
        out bool sized,
        double offsetEastM = 0.0,
        double offsetNorthM = 0.0)
    {
        double ground = MetresToInternal(tree.GroundElevationM);
        XYZ location = new(MetresToInternal(tree.EastM + offsetEastM), MetresToInternal(tree.NorthM + offsetNorthM), ground);

        FamilyInstance instance = document.Create.NewFamilyInstance(location, symbol, level, StructuralType.NonStructural);

        Parameter? offset = instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
        bool lifted = offset is { IsReadOnly: false } && offset.Set(ground - level.ProjectElevation);

        bool tall = SetLength(instance, PlantingFamilies.HeightParameter(tree.FoliageType), tree.HeightM);
        bool wide = SetLength(instance, TreeFamily.CrownRadiusParameter, tree.CrownRadiusM);
        sized = lifted && tall && wide;
        return instance;
    }

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
