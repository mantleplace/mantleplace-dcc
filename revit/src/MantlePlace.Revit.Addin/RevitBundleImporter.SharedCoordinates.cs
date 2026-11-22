using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private void SetSharedCoordinates(ImportStep step)
    {
        if (step.SurveyPoint is not { Origin.IsUsable: true } placement)
        {
            return;
        }

        if (Anchoring.Kind == AnchorDecisionKind.Offset)
        {
            Say(AdjacentBundle.Describe(Anchoring));
            return;
        }

        GeoOrigin origin = placement.Origin;
        ForgeTypeId originUnit = ToUnitTypeId(origin.LinearUnit);

        ImportFailureSwallower swallower = new("Setting the shared coordinates");
        using Transaction transaction = BeginTransaction("Mantle Place: shared coordinates", swallower);
        ProjectPosition position = new(
            UnitUtils.ConvertToInternalUnits(origin.Easting!.Value, originUnit),
            UnitUtils.ConvertToInternalUnits(origin.Northing!.Value, originUnit),
            UnitUtils.ConvertToInternalUnits(placement.ElevationM, UnitTypeId.Meters),
            placement.AngleRadians);
        _document.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, position);

        ProjectAnchorStore.Write(_document, origin.Epsg!.Value);
        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        Say($"Set shared coordinates from the manifest (EPSG:{origin.Epsg}).");
    }

    private static ForgeTypeId ToUnitTypeId(LinearUnit unit) => unit switch
    {
        LinearUnit.UsSurveyFoot => UnitTypeId.UsSurveyFeet,
        LinearUnit.InternationalFoot => UnitTypeId.Feet,
        _ => UnitTypeId.Meters,
    };
}
