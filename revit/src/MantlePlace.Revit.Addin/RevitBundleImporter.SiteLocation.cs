using System.Globalization;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The site-location step: the published latitude and longitude, applied verbatim.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Sets the project's latitude and longitude, which is what places the sun in every view and
    /// every renderer. The values are the manifest's; the planner already converted them to the
    /// radians Revit takes and checked they are on the globe (HPS-33).
    /// </summary>
    /// <remarks>
    /// The time zone is not set. Revit adjusts it by itself when either setter runs — documented on
    /// both — so the zone the project ends with is Revit's choice, and the log says what it chose
    /// rather than leaving the curator to assume the plugin decided it.
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
        site.Latitude = placement.LatitudeRadians;
        site.Longitude = placement.LongitudeRadians;
        double timeZone = site.TimeZone;
        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        Say(string.Format(
            CultureInfo.InvariantCulture,
            "Set the site location from the manifest: latitude {0}°, longitude {1}°. The bundle publishes "
            + "no time zone, and Revit set the project's to UTC{2:+0.##;-0.##;+0} from these coordinates "
            + "itself — check it under Manage ▸ Location before a sun study that depends on the clock.",
            placement.LatitudeDeg,
            placement.LongitudeDeg,
            timeZone));
    }
}
