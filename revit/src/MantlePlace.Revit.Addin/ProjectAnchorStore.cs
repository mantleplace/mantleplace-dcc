using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Reads and writes what a project holds for <see cref="AdjacentBundle"/>: the shared coordinates
/// it was given and the EPSG code they were given in.
/// </summary>
/// <remarks>
/// The position itself is read live from the project, so a curator who moves the survey point on
/// purpose moves the anchor with it; only the code, which Revit has nowhere to keep, is stored
/// (<see cref="ProjectAnchorStorage"/>).
/// </remarks>
internal static class ProjectAnchorStore
{
    /// <summary>
    /// The project's anchor, or <c>null</c> when it holds no Mantle Place import. A project with a
    /// provenance record but no code (one imported into before the code was recorded) is an anchor
    /// whose <see cref="ProjectAnchor.Epsg"/> is <c>null</c>.
    /// </summary>
    internal static ProjectAnchor? Read(Document document)
    {
        Entity recorded = document.ProjectInformation.GetEntity(AnchorSchema());
        int? epsg = null;
        if (recorded.IsValid())
        {
            int code = recorded.Get<int>(ProjectAnchorStorage.EpsgField);
            epsg = code > 0 ? code : null;
        }
        else if (!document.ProjectInformation.GetEntity(AttributionNotes.ProvenanceSchema()).IsValid())
        {
            return null;
        }

        ProjectPosition position = document.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        return new ProjectAnchor(
            epsg,
            UnitUtils.ConvertFromInternalUnits(position.EastWest, UnitTypeId.Meters),
            UnitUtils.ConvertFromInternalUnits(position.NorthSouth, UnitTypeId.Meters),
            position.Angle);
    }

    /// <summary>Records the code the project's position was set from. Inside the caller's transaction.</summary>
    internal static void Write(Document document, int epsg)
    {
        Entity entity = new(AnchorSchema());
        entity.Set(ProjectAnchorStorage.EpsgField, epsg);
        document.ProjectInformation.SetEntity(entity);
    }

    private static Schema AnchorSchema()
    {
        if (Schema.Lookup(ProjectAnchorStorage.SchemaGuid) is { } existing)
        {
            return existing;
        }

        SchemaBuilder builder = new(ProjectAnchorStorage.SchemaGuid);
        builder.SetSchemaName(ProjectAnchorStorage.SchemaName);
        builder.SetVendorId(ProjectAnchorStorage.VendorId);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Vendor);
        builder.SetDocumentation("The EPSG code the project's shared coordinates were set from by the first Mantle Place bundle imported into it.");
        builder.AddSimpleField(ProjectAnchorStorage.EpsgField, typeof(int));
        return builder.Finish();
    }
}
