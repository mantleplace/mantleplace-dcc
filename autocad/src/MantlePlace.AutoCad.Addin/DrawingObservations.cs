using Autodesk.AutoCAD.DatabaseServices;
using MantlePlace.AutoCad;

namespace MantlePlace.AutoCad.Addin;

internal static class DrawingObservations
{
    internal static DrawingZone Zone(string code)
    {
        DrawingZone zone = new(code ?? string.Empty, null);
        if (!zone.IsSet)
        {
            return zone;
        }

        try
        {
            GeoCoordinateSystem system = GeoCoordinateSystem.Create(code);
            return zone with { Epsg = system.EPSGcode > 0 ? system.EPSGcode : null };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return zone;
        }
    }

    internal static DrawingGeolocation Geolocation(Database database)
    {
        ObjectId id;
        try
        {
            id = database.GeoDataObject;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return DrawingGeolocation.None;
        }

        if (id.IsNull)
        {
            return DrawingGeolocation.None;
        }

        string definition;
        using (Transaction transaction = database.TransactionManager.StartOpenCloseTransaction())
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not GeoLocationData location)
            {
                return DrawingGeolocation.None;
            }

            definition = location.CoordinateSystem ?? string.Empty;
        }

        if (definition.Length == 0)
        {
            return DrawingGeolocation.Located("a geographic location with no coordinate system", null);
        }

        try
        {
            GeoCoordinateSystem system = GeoCoordinateSystem.Create(definition);
            return DrawingGeolocation.Located(
                string.IsNullOrWhiteSpace(system.ID) ? definition : system.ID,
                system.EPSGcode > 0 ? system.EPSGcode : null);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return DrawingGeolocation.Located(definition.Length > 80 ? definition[..80] + "..." : definition, null);
        }
    }
}
