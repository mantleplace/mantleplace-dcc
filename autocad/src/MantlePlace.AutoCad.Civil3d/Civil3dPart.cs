using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace MantlePlace.AutoCad.Civil3d;

public static class Civil3dPart
{
    public static string Describe()
        => "Civil 3D API " + typeof(CivilApplication).Assembly.GetName().Version;

    public static string ReadZoneCode(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return CivilDocument.GetCivilDocument(database).Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode ?? string.Empty;
    }

    public static string ReadDrawingUnits(Database database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return CivilDocument.GetCivilDocument(database).Settings.DrawingSettings.UnitZoneSettings.DrawingUnits.ToString();
    }

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
