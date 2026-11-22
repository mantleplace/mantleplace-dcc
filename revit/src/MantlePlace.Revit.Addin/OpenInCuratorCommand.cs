using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class OpenInCuratorCommand : IExternalCommand
{
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

    private static string? ReadOrderId(Document document)
    {
        if (Schema.Lookup(ProvenanceStorage.SchemaGuid) is not { } schema)
        {
            return null;
        }

        return AttributionNotes.ReadProvenance(document, schema)?.OrderId;
    }

    private static SiteLocationPoint ReadSiteLocation(Document document)
        => SiteLocationPoint.FromRadians(document.SiteLocation.Latitude, document.SiteLocation.Longitude);

    private static IReadOnlyList<VaultBundle>? ListVaultBounded()
    {
        if (MantlePlaceApplication.Session.State != AuthState.Authenticated)
        {
            return null;
        }

        try
        {
            using CancellationTokenSource timeout = new(ListingWait);

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
        };

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

        if (offered > 0)
        {
            dialog.DefaultButton = results[0];
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
