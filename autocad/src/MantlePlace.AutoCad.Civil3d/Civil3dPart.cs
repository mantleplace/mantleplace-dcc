using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace MantlePlace.AutoCad.Civil3d;

/// <summary>
/// The Civil 3D half of the plugin: everything that needs a Civil 3D assembly lives in this assembly
/// and nowhere else. The shell reaches it by reflection, behind the presence test, so plain AutoCAD
/// never resolves a Civil 3D type. It observes and applies; every decision is in the pure core
/// (<c>AutoCadImportPlanner</c>).
/// </summary>
public static class Civil3dPart
{
    /// <summary>
    /// What the part can say about the Civil 3D it loaded into, which resolving a Civil 3D type proves:
    /// the version of the Civil 3D API assembly it is running against.
    /// </summary>
    public static string Describe()
        => "Civil 3D API " + typeof(CivilApplication).Assembly.GetName().Version;

    /// <summary>
    /// The drawing's coordinate zone code, in Civil 3D's own spelling, read from its drawing settings.
    /// Read only: ⛔ the plugin never sets the zone (autocad/docs/api-record.md). A drawing with none reads
    /// <c>"."</c>.
    /// </summary>
    public static string ReadZoneCode(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return CivilDocument.GetCivilDocument(database).Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode ?? string.Empty;
    }

    /// <summary>
    /// Builds a TIN surface from the surface the LandXML file at <paramref name="landXmlPath"/> names
    /// <paramref name="landXmlSurface"/>, named
    /// <paramref name="stem"/> (or the stem and a number, when the drawing has a surface of that name
    /// already), on <paramref name="layer"/>, which the shell has made. Civil 3D reads the file and builds
    /// the triangles; the plugin hands it the path and works nothing out.
    /// </summary>
    /// <returns>The name the surface was given, and the points Civil 3D read.</returns>
    public static (string Name, int Points) BuildSurfaceFromLandXml(Database database, string stem, string landXmlPath, string landXmlSurface, string layer)
    {
        ArgumentNullException.ThrowIfNull(database);

        using Transaction transaction = database.TransactionManager.StartTransaction();
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId existing in CivilDocument.GetCivilDocument(database).GetSurfaceIds())
        {
            if (transaction.GetObject(existing, OpenMode.ForRead) is CivilSurface named)
            {
                taken.Add(named.Name);
            }
        }

        string name = stem;
        for (int n = 2; taken.Contains(name); n++)
        {
            name = $"{stem}-{n}";
        }

        ObjectId id = TinSurface.CreateFromLandXML(database, name, landXmlPath, landXmlSurface);
        TinSurface surface = (TinSurface)transaction.GetObject(id, OpenMode.ForWrite);
        surface.Layer = layer;
        int points = surface.GetGeneralProperties().NumberOfPoints;
        transaction.Commit();
        return (name, points);
    }
}
