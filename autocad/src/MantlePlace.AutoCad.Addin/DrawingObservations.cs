using Autodesk.AutoCAD.DatabaseServices;
using MantlePlace.AutoCad;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// What the import reads of the drawing: its geographic location, observed and not decided on. Whether
/// that location agrees with the order is <see cref="AutoCadImportPlanner"/>'s decision, in the pure core.
/// </summary>
/// <remarks>
/// The location is the drawing's <c>GeoLocationData</c>, which plain AutoCAD's GEOGRAPHICLOCATION sets and
/// Civil 3D's drawing zone sets with it. Its coordinate system is a code or a definition; AutoCAD's own
/// <c>GeoCoordinateSystem</c> names its code and the EPSG code it resolves to, so nothing about a
/// coordinate system is worked out here. A location whose coordinate system AutoCAD cannot open is still a
/// location: it is reported with no EPSG code, and the plan refuses rather than guessing.
/// </remarks>
internal static class DrawingObservations
{
    /// <summary>
    /// The drawing's Civil 3D coordinate zone, from the code the Civil 3D part read: the code as read, and
    /// the EPSG code AutoCAD's own coordinate library gives for it. Civil 3D's zone codes are that
    /// library's codes, so the code is never mapped here; a code with no zone, or one AutoCAD gives no EPSG
    /// code for, is reported with none, and the plan decides.
    /// </summary>
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
            // AutoCAD answers a drawing with no geographic location by throwing (eNotInDatabase / eKeyNotFound).
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
            // A definition can be a whole XML document; the refusal quotes its start, not all of it.
            return DrawingGeolocation.Located(definition.Length > 80 ? definition[..80] + "..." : definition, null);
        }
    }
}
