using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The shared-coordinates step: the published survey point, applied verbatim.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Publishes the pre-derived survey point. The values are applied verbatim — this method does
    /// no projection and no arithmetic beyond the unit conversion Revit's API requires (HPS-33).
    /// </summary>
    /// <remarks>
    /// The elevation and the angle used to be literal zeros here. Both are now
    /// <see cref="SurveyPointPlacement"/>'s, decided in the pure core where the headless suite can
    /// assert them; and the plan coordinates go in under their OWN unit rather than as assumed
    /// metres, because <c>revit.georeference.origin.projected</c> publishes a State-Plane foot
    /// origin on the foot tiers.
    /// </remarks>
    private void SetSharedCoordinates(ImportStep step)
    {
        if (step.SurveyPoint is not { Origin.IsUsable: true } placement)
        {
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
        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say($"Set shared coordinates from the manifest (EPSG:{origin.Epsg}).");
    }

    /// <summary>
    /// Maps the manifest's unit to Revit's measurement vocabulary. <see cref="LinearUnit.Unspecified"/>
    /// is metric, the reading every other unit site in this plugin takes for an unstated unit.
    /// </summary>
    private static ForgeTypeId ToUnitTypeId(LinearUnit unit) => unit switch
    {
        LinearUnit.UsSurveyFoot => UnitTypeId.UsSurveyFeet,
        LinearUnit.InternationalFoot => UnitTypeId.Feet,
        _ => UnitTypeId.Meters,
    };
}
