using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
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

        Say(placement.LogLine(projectTimeZone));
    }
}
