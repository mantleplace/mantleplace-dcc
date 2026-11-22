namespace MantlePlace.Core;

public enum SubDivisionMaterialRoute
{
    Instance,

    Type,

    Refused,
}

public static class SubDivisionMaterial
{
    private const string TypeSuffix = " sub-division";

    public static SubDivisionMaterialRoute Route(bool instanceMaterialWritable, bool typedAsToposolid)
        => instanceMaterialWritable ? SubDivisionMaterialRoute.Instance
            : typedAsToposolid ? SubDivisionMaterialRoute.Type
            : SubDivisionMaterialRoute.Refused;

    public static string TypeName(string materialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        return materialName + TypeSuffix;
    }

    public static bool NeedsRetype(SubDivisionMaterialRoute route, string? currentTypeName, string materialName)
        => route == SubDivisionMaterialRoute.Type
            && !string.Equals(currentTypeName, TypeName(materialName), StringComparison.Ordinal);

    public static bool TakesTypeAtCut(SubDivisionMaterialRoute route, bool drapePlanned)
        => drapePlanned && route == SubDivisionMaterialRoute.Type;

    public static bool IsLayeredImageryType(string? typeName)
        => typeName is not null
            && typeName.StartsWith(DrapeLayering.ImageryNamePrefix + " ", StringComparison.Ordinal);
}
