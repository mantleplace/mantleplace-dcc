namespace MantlePlace.Revit.Core;

/// <summary>How a subdivision is given its drape material. Decided by what the element can take.</summary>
public enum SubDivisionMaterialRoute
{
    /// <summary>Write the subdivision's own <c>TOPOSOLID_SUBDIVIDE_MATERIAL</c>. The 2025 shape.</summary>
    Instance,

    /// <summary>Retype the subdivision onto a toposolid type whose top layer wears the material. 2026 and later.</summary>
    Type,

    /// <summary>Neither is available, so the subdivision is counted as refused.</summary>
    Refused,
}

/// <summary>
/// Which of the two ways a subdivision can wear the photograph applies to one element, and what the
/// type that carries it is called. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A subdivision's shape depends on the Revit version, and the choice is made from the shape,
/// never from the version number.</b> Measured in Revit 2025 (2026-08-25): a subdivision is typeless,
/// its <c>GetTypeId()</c> is invalid, <c>ChangeTypeId</c> throws "This Element cannot have type
/// assigned", and the material is an instance parameter. Measured in Revit 2026 and 2027
/// (2026-09-19): a subdivision is a <c>Toposolid</c> typed with the document's default toposolid type,
/// the instance parameter is absent, and a retype onto a duplicated type holds. 2026 added a
/// <c>CreateSubDivision</c> overload that takes the type. The plugin compiles against 2025's API, so
/// it calls the overload that takes the default and retypes afterwards: in the transaction that cut
/// the subdivision when the drape is planned (<see cref="TypeAtCut"/>), and in the drape otherwise.
/// </para>
/// <para>
/// The instance write is asked first because it is free, and a retype is not: 33 of them took about
/// 190 s of a real 2027 import on a 74,852-point terrain, most of it in the commit
/// (<see cref="SlowStepNotice.ForSubDivisionRetypes"/>).
/// </para>
/// </remarks>
public static class SubDivisionMaterial
{
    /// <summary>What every subdivision type this plugin makes ends with.</summary>
    private const string TypeSuffix = " sub-division";

    /// <summary>The route for one subdivision, from what it was found to accept.</summary>
    /// <param name="instanceMaterialWritable">It has a <c>TOPOSOLID_SUBDIVIDE_MATERIAL</c> that is not read-only.</param>
    /// <param name="typedAsToposolid">Its type is a <c>ToposolidType</c>.</param>
    public static SubDivisionMaterialRoute Route(bool instanceMaterialWritable, bool typedAsToposolid)
        => instanceMaterialWritable ? SubDivisionMaterialRoute.Instance
            : typedAsToposolid ? SubDivisionMaterialRoute.Type
            : SubDivisionMaterialRoute.Refused;

    /// <summary>
    /// The toposolid type that carries <paramref name="materialName"/> on a subdivision: one type per
    /// distinct material, found again by name on a re-import.
    /// </summary>
    /// <remarks>
    /// ⛔ Never the material's name as it stands. Under flat shading a subdivision with no keyword
    /// shares the ground's material, whose name is the terrain's imagery type's name. Finding that type
    /// by name would put the subdivision on the terrain's own layer stack, and editing it would edit
    /// the terrain.
    /// </remarks>
    public static string TypeName(string materialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        return materialName + TypeSuffix;
    }

    /// <summary>
    /// Whether a subdivision on <paramref name="currentTypeName"/> will be retyped to wear
    /// <paramref name="materialName"/> — the count the drape announces before it starts.
    /// </summary>
    /// <remarks>
    /// The same comparison the drape makes when it retypes, so the announcement and the work cannot
    /// disagree: a re-import whose subdivisions are on their types announces nothing, and one whose
    /// renderer keyword changed between builds announces the subdivisions it will move.
    /// </remarks>
    public static bool NeedsRetype(SubDivisionMaterialRoute route, string? currentTypeName, string materialName)
        => route == SubDivisionMaterialRoute.Type
            && !string.Equals(currentTypeName, TypeName(materialName), StringComparison.Ordinal);

    /// <summary>
    /// Whether a subdivision just cut is given its own imagery type in the transaction that cut it,
    /// rather than retyped by the drape afterwards.
    /// </summary>
    /// <param name="route">What the new element was found to accept, asked after the cut returned.</param>
    /// <param name="drapePlanned">Whether the plan's drape will run (<see cref="ImportStep.DrapePlanned"/>).</param>
    /// <remarks>
    /// Only a typed element, and only when a drape will fill the type's photograph layer. The type is
    /// the one the drape would have retyped it onto, named for smooth shading because the drape turns
    /// smoothing on (ADR 0008); a drape that finds smoothing refused retypes it as before
    /// (<see cref="NeedsRetype"/>). A typeless element keeps its instance material, which the drape
    /// writes.
    /// </remarks>
    public static bool TypeAtCut(SubDivisionMaterialRoute route, bool drapePlanned)
        => drapePlanned && route == SubDivisionMaterialRoute.Type;

    /// <summary>
    /// Whether <paramref name="typeName"/>'s top layer is already this plugin's thin photograph layer:
    /// a terrain imagery type or a subdivision type, made for any bundle.
    /// </summary>
    /// <remarks>
    /// ⛔ Such a type is re-pointed, never split again. Splitting it would stack a second thin layer
    /// or be refused as too thin, and a subdivision can be on one: from 2026 a new subdivision takes
    /// the document's default toposolid type, whatever that happens to be.
    /// </remarks>
    public static bool IsLayeredImageryType(string? typeName)
        => typeName is not null
            && typeName.StartsWith(DrapeLayering.ImageryNamePrefix + " ", StringComparison.Ordinal);
}
