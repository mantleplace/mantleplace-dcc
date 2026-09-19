using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The site-location step: the published latitude, longitude and time zone, applied verbatim.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Sets the project's latitude, longitude and time zone, which is what places the sun in every
    /// view and every renderer. The values are the manifest's; the planner already converted them to
    /// the radians and the hours Revit takes and checked they are on the globe (HPS-33).
    /// </summary>
    /// <remarks>
    /// The zone is written after the coordinates, in the same transaction. Revit recalculates it from
    /// the new coordinates when either setter runs — documented on both — and a zone derived from
    /// longitude is derivation whoever does it. So the published zone is written last, and when the
    /// bundle publishes none, the project's own is read first and written back.
    /// </remarks>
    private void SetSiteLocation(ImportStep step)
    {
        if (step.SiteLocation is not { } placement)
        {
            return;
        }

        ImportFailureSwallower swallower = new("Setting the site location");
        using Transaction transaction = BeginTransaction("Mantle Place: site location", swallower);
        SiteLocation site = _document.SiteLocation;
        double projectTimeZone = site.TimeZone;
        site.Latitude = placement.LatitudeRadians;
        site.Longitude = placement.LongitudeRadians;
        site.TimeZone = placement.TimeZoneToWrite(projectTimeZone);
        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        Say(placement.Report(projectTimeZone));
    }
}
