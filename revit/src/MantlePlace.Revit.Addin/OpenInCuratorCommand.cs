using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>"Open in Curator": opens the web curator on the extent the project already knows.</summary>
/// <remarks>
/// <para>
/// Not a placement: nothing is written to the project. The extent comes from the order id the last
/// Mantle Place import left on Project Information, whose extent the vault record carries, or else
/// from the project's site latitude and longitude with a size the curator picks. A project with
/// neither opens the plain curator. Every one of those decisions, and the link, is
/// <see cref="CuratorExtents"/> and <see cref="CuratorLink"/> in the pure core, asserted without
/// Revit; what is left here is reading the two facts out of the model, one bounded vault listing,
/// the size dialog and the shell call.
/// </para>
/// <para>
/// It opens no transaction and is reachable with no project open, which opens the plain curator.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class OpenInCuratorCommand : IExternalCommand
{
    /// <summary>
    /// How long the command waits for the vault listing. It runs on Revit's thread, so the wait is
    /// short: a listing that has not answered by then is treated as unavailable, and the curator is
    /// told, rather than Revit left frozen on a slow network.
    /// </summary>
    private static readonly TimeSpan ListingWait = TimeSpan.FromSeconds(6);

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        Document? document = commandData.Application.ActiveUIDocument?.Document;
        string? orderId = document is null ? null : ReadOrderId(document);
        SiteLocationPoint? site = document is null ? null : ReadSiteLocation(document);

        CuratorProjectState state = new()
        {
            ImportedOrderId = orderId,
            VaultBundles = string.IsNullOrWhiteSpace(orderId) ? null : ListVaultBounded(),
            Site = site,
        };

        CuratorResolution resolution = CuratorExtents.Resolve(state);
        CuratorExtent? extent = resolution.Extent;

        switch (resolution.Source)
        {
            case CuratorExtentSource.SiteLocation:
                if (AskSize(resolution, site!.Value, document!.SiteLocation.PlaceName) is not { } chosen)
                {
                    return Result.Cancelled;
                }

                extent = chosen;
                break;

            case CuratorExtentSource.None when resolution.OrderExtentUnavailable:
                // The project names an import, the extent could not be read, and there is no site
                // location to fall back to: say so, then open what can be opened.
                new TaskDialog("Mantle Place")
                {
                    MainInstruction = "This project's import extent could not be read.",
                    MainContent = UnavailableReason() + " Opening the curator without an extent.",
                }.Show();
                break;
        }

        string link = CuratorLink.Build(MantlePlaceApplication.Endpoints.ApiBaseUrl, extent);
        if (!ShellLauncher.TryOpen(link, out string refused))
        {
            new TaskDialog("Mantle Place")
            {
                MainInstruction = "Could not open your browser.",
                MainContent = $"Open it yourself: {refused}",
            }.Show();
        }

        return Result.Succeeded;
    }

    /// <summary>The order id Project Information records, or <c>null</c> for a project never imported into.</summary>
    private static string? ReadOrderId(Document document)
    {
        // Lookup, not ProvenanceSchema(): a project this add-in never imported into has no schema
        // to read, and a read has no business building one.
        if (Schema.Lookup(ProvenanceStorage.SchemaGuid) is not { } schema)
        {
            return null;
        }

        return AttributionNotes.ReadProvenance(document, schema)?.OrderId;
    }

    /// <summary>
    /// The project's site latitude and longitude, in degrees. The values are Revit's own
    /// (radians); whether they are a place at all is <see cref="SiteLocationPoint.IsLocated"/>'s.
    /// </summary>
    private static SiteLocationPoint ReadSiteLocation(Document document)
        => SiteLocationPoint.FromRadians(document.SiteLocation.Latitude, document.SiteLocation.Longitude);

    /// <summary>
    /// The vault listing, or <c>null</c> when the curator is signed out, the call failed or it did
    /// not answer in <see cref="ListingWait"/>.
    /// </summary>
    private static IReadOnlyList<VaultBundle>? ListVaultBounded()
    {
        if (MantlePlaceApplication.Session.State != AuthState.Authenticated)
        {
            return null;
        }

        try
        {
            using CancellationTokenSource timeout = new(ListingWait);

            // Task.Run so the continuation never wants Revit's thread, which is the one waiting.
            Task<(VaultListing? Listing, string? Error)> listing =
                Task.Run(() => MantlePlaceApplication.Vault.ListAsync(timeout.Token), timeout.Token);

            return listing.Wait(ListingWait) ? listing.Result.Listing?.Bundles : null;
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
        {
            return null;
        }
    }

    private static string UnavailableReason()
        => MantlePlaceApplication.Session.State == AuthState.Authenticated
            ? "The vault did not list that order with an extent."
            : "Sign in to Mantle Place and try again: the extent comes from your vault.";

    /// <summary>
    /// Asks which of the offered sizes to draw around the site location; <c>null</c> when the curator
    /// cancels.
    /// </summary>
    private static CuratorExtent? AskSize(CuratorResolution resolution, SiteLocationPoint site, string placeName)
    {
        string place = string.IsNullOrWhiteSpace(placeName)
            ? string.Empty
            : $"{placeName}\n";

        string content = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}Site location: {1:0.0000}, {2:0.0000} (latitude, longitude).",
            place,
            site.LatitudeDegrees,
            site.LongitudeDegrees);

        if (resolution.OrderExtentUnavailable)
        {
            content += "\n\nThe extent of this project's import could not be read, so it is not used. "
                + UnavailableReason();
        }

        TaskDialog dialog = new("Mantle Place")
        {
            MainInstruction = "How much around the project's site location?",
            MainContent = content,
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink1,
        };

        // A Revit task dialog has four command links, and the core offers no more than that.
        TaskDialogCommandLinkId[] links =
        [
            TaskDialogCommandLinkId.CommandLink1,
            TaskDialogCommandLinkId.CommandLink2,
            TaskDialogCommandLinkId.CommandLink3,
            TaskDialogCommandLinkId.CommandLink4,
        ];

        TaskDialogResult[] results =
        [
            TaskDialogResult.CommandLink1,
            TaskDialogResult.CommandLink2,
            TaskDialogResult.CommandLink3,
            TaskDialogResult.CommandLink4,
        ];

        int offered = Math.Min(links.Length, resolution.SizeChoices.Count);
        for (int i = 0; i < offered; i++)
        {
            dialog.AddCommandLink(links[i], resolution.SizeChoices[i].Label);
        }

        TaskDialogResult result = dialog.Show();
        for (int i = 0; i < offered; i++)
        {
            if (result == results[i])
            {
                return resolution.SizeChoices[i].Extent;
            }
        }

        return null;
    }
}
