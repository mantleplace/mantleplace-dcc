using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal static class ProjectAnchorStore
{
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
