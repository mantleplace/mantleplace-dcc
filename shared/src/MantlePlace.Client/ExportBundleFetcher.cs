using System.Collections.Concurrent;
using MantlePlace.Core;

namespace MantlePlace.Client;

/// <summary>
/// The vault routes and the cache an export's bundle is fetched through: the same ones the vault
/// browser's Import uses. The seam exists so the fetch's order is asserted without a platform.
/// </summary>
public interface IExportVault
{
    /// <summary>The curator's bundles, with each one's manifest version and integrity facts.</summary>
    Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken);

    /// <summary>The cached zip when the cache holds a valid copy of <paramref name="bundle"/> (fully hashed), else <c>null</c>.</summary>
    string? CachedZip(VaultBundle bundle);

    /// <summary>One materialize start, read as the route an Import press takes.</summary>
    Task<ImportRouteDecision> RouteImportAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a complete order's Revit view, or the whole bundle, into the cache; a 3.0 order's
    /// download at the platform's recommendation, since an export names no level.
    /// </summary>
    Task<CompleteDownload> DownloadCompleteAsync(
        VaultBundle bundle,
        IReadOnlyCollection<string>? delivered,
        CancellationToken cancellationToken);
}

/// <summary>
/// A sent order downloaded again at the level the curator chose: ready with the slot's zip, or the
/// reason it is not (the slot then still holds the level it had).
/// </summary>
/// <param name="Ready">Whether the slot holds the chosen level, fully checked.</param>
/// <param name="ZipPath">The slot's zip when ready.</param>
/// <param name="Message">Why not, when not ready.</param>
public readonly record struct LevelFetch(bool Ready, string ZipPath, string Message)
{
    internal static LevelFetch Failed(string message) => new(false, string.Empty, message);
}

/// <summary>The real vault and the real cache.</summary>
public sealed class VaultExportSource : IExportVault
{
    private static readonly IProgress<string> Quiet = new Progress<string>(_ => { });

    private readonly VaultClient _vault;
    private readonly BundleCache _cache;
    private readonly ConcurrentDictionary<string, VaultBundle> _listed = new(StringComparer.Ordinal);

    public VaultExportSource(VaultClient vault, BundleCache cache)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken)
    {
        (VaultListing? listing, string? error) = await _vault.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (VaultBundle row in listing?.Bundles ?? [])
        {
            _listed[row.OrderId] = row;
        }

        return (listing, error);
    }

    /// <summary>
    /// The row the last fetch listed for <paramref name="orderId"/>, or <c>null</c>: what the window a send
    /// opens builds its level control from (<see cref="LevelControl.For"/>), without a second listing.
    /// </summary>
    public VaultBundle? Listed(string orderId)
        => _listed.TryGetValue(orderId, out VaultBundle? row) ? row : null;

    /// <summary>
    /// Downloads a sent 3.0 order at the level the curator chose in the window the send opened, into the
    /// host's slot at that level, and answers the slot's zip once it is fully checked. Never throws.
    /// </summary>
    public async Task<LevelFetch> FetchLevelAsync(
        string orderId,
        FidelityLevel level,
        IProgress<string> say,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(say);
        try
        {
            if (Listed(orderId) is not { Levels: not null } bundle)
            {
                return LevelFetch.Failed("this order offers no levels to choose from");
            }

            string slot = DownloadLevels.SlotLevel(bundle, level);
            CacheEntry held = _cache.Inspect(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion, slot);
            if (held.State == CacheState.CachedValid)
            {
                return new LevelFetch(true, held.Layout.BundleZipPath, string.Empty);
            }

            CompleteDownload download = await _vault.DownloadCompleteAsync(
                bundle, delivered: null, _cache, say, cancellationToken, level).ConfigureAwait(false);
            CacheEntry entry = _cache.Inspect(
                download.Bundle.OrderId, download.Bundle.SizeBytes, download.Bundle.Sha256, download.Bundle.ManifestVersion, slot);
            if (!download.Ready || entry.State != CacheState.CachedValid)
            {
                return LevelFetch.Failed(download.Ready ? entry.Describe() : download.Message);
            }

            return new LevelFetch(true, entry.Layout.BundleZipPath, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return LevelFetch.Failed("the download was stopped");
        }
#pragma warning disable CA1031 // The window must go on at the level it holds, whatever the fault was.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return LevelFetch.Failed(ex.Message);
        }
    }

    public string? CachedZip(VaultBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        // The full hash, as the vault browser's pre-import gate does: once per import. An export names
        // no level, so a 3.0 order's slot is the one at the platform's recommendation.
        CacheEntry entry = _cache.Inspect(
            bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion, DownloadLevels.SlotLevel(bundle));
        return entry.State == CacheState.CachedValid ? entry.Layout.BundleZipPath : null;
    }

    public Task<ImportRouteDecision> RouteImportAsync(string orderId, CancellationToken cancellationToken)
        => _vault.RouteImportAsync(orderId, cancellationToken);

    public Task<CompleteDownload> DownloadCompleteAsync(
        VaultBundle bundle,
        IReadOnlyCollection<string>? delivered,
        CancellationToken cancellationToken)
        => _vault.DownloadCompleteAsync(bundle, delivered, _cache, Quiet, cancellationToken);
}

/// <summary>
/// Fetches the bundle an export names into the cache, the way the vault browser's Import does, and
/// says how it went in the terms the export desk reads (<see cref="ExportFetchResult"/>).
/// </summary>
/// <remarks>
/// <para>
/// In order: the listing (the bundle must be the curator's, and its manifest version must be one the
/// reader takes, checked before a byte is downloaded); the cache (a valid copy is used as it is); one
/// materialize start, which tells a complete order from an on-demand one (<see cref="CompleteOrders"/>);
/// then the complete order's download, or the Prepare's materialize, poll, re-list and download.
/// </para>
/// <para>
/// The moment it knows it must wait for a build, it says so (<c>onBuilding</c>), so the export moves
/// to <c>waiting_for_bundle</c> while the build runs rather than after it. A build that outlasts the
/// poll's budget (<c>HPS-25</c>) ends <see cref="ExportFetchKind.StillBuilding"/>: the order row's
/// change is then what wakes the next fetch.
/// </para>
/// <para>
/// Never throws: any fault is a failure with its message, so the export can say what happened.
/// </para>
/// </remarks>
public sealed class ExportBundleFetcher
{
    private readonly IExportVault _vault;
    private readonly IPrepareSteps _prepare;

    public ExportBundleFetcher(IExportVault vault, IPrepareSteps prepare)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
    }

    public async Task<ExportFetchResult> FetchAsync(string orderId, Action onBuilding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onBuilding);
        try
        {
            return await FetchCoreAsync(orderId, onBuilding, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExportFetchResult.Failed(ExportReasons.DownloadFailed, "The fetch was stopped: Revit left the connection.");
        }
#pragma warning disable CA1031 // The export must end with a reason, whatever the fault was.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ExportFetchResult.Failed(ExportReasons.DownloadFailed, $"The bundle could not be fetched: {ex.Message}");
        }
    }

    private async Task<ExportFetchResult> FetchCoreAsync(string orderId, Action onBuilding, CancellationToken token)
    {
        (VaultListing? listing, string? listError) = await _vault.ListAsync(token).ConfigureAwait(false);
        if (listError is not null || listing is null)
        {
            return ExportFetchResult.Failed(ExportReasons.DownloadFailed, listError ?? "The vault listing could not be read.");
        }

        if (listing.Bundles.FirstOrDefault(row => string.Equals(row.OrderId, orderId, StringComparison.Ordinal)) is not { } bundle)
        {
            return ExportFetchResult.Failed(
                ExportReasons.BundleNotInVault,
                "This bundle is not in the vault this Revit is signed in to.");
        }

        if (bundle.Status is BundleStatus.Refunded or BundleStatus.Failed)
        {
            return ExportFetchResult.Failed(
                ExportReasons.BundleUnavailable,
                "This bundle is no longer available in your vault, so Revit cannot import it.");
        }

        if (!ExportBundles.CanRead(bundle.ManifestVersion))
        {
            return ExportFetchResult.Unreadable(bundle.ManifestVersion);
        }

        if (_vault.CachedZip(bundle) is { } cached)
        {
            return ExportFetchResult.Ready(cached, bundle.ManifestVersion);
        }

        ImportRouteDecision route = await _vault.RouteImportAsync(orderId, token).ConfigureAwait(false);
        if (route.Route == ImportRoute.Refused)
        {
            return ExportFetchResult.Failed(ExportReasons.DownloadFailed, route.Message);
        }

        if (route.Route == ImportRoute.DownloadView)
        {
            CompleteDownload download = await _vault.DownloadCompleteAsync(bundle, route.Delivered, token).ConfigureAwait(false);
            if (!download.NeedsPrepare)
            {
                return download.Ready && _vault.CachedZip(download.Bundle) is { } zip
                    ? ExportFetchResult.Ready(zip, download.Bundle.ManifestVersion)
                    : ExportFetchResult.Failed(ExportReasons.DownloadFailed, download.Message);
            }
        }

        return await PrepareAsync(bundle, onBuilding, token).ConfigureAwait(false);
    }

    // The Prepare's halves, in the watcher's order: start (or join), poll, re-list, download.
    private async Task<ExportFetchResult> PrepareAsync(VaultBundle bundle, Action onBuilding, CancellationToken token)
    {
        onBuilding();

        VaultResult<MaterializeStart> start = await _prepare.StartAsync(bundle.OrderId, token).ConfigureAwait(false);
        if (!start.Succeeded)
        {
            return ExportFetchResult.Failed(ExportReasons.DownloadFailed, start.Error!);
        }

        MaterializeStart begun = start.Value!.Value;
        if (begun.Outcome != MaterializeStartOutcome.NothingToDo)
        {
            PollOutcome polled = await _prepare.PollAsync(
                bundle.OrderId,
                MaterializeJobs.RequestedForPolling(begun),
                new Progress<MaterializeStatus>(_ => { }),
                token).ConfigureAwait(false);

            if (polled.OutOfPolls)
            {
                return ExportFetchResult.StillBuilding(polled.Result.Error ?? "The bundle is still being built.");
            }

            if (!polled.Result.Succeeded)
            {
                return ExportFetchResult.Failed(ExportReasons.DownloadFailed, polled.Result.Error!);
            }
        }

        PreparedDownload download = await _prepare.DownloadAsync(bundle, new Progress<string>(_ => { }), token).ConfigureAwait(false);
        if (!download.Ready)
        {
            return ExportFetchResult.Failed(ExportReasons.DownloadFailed, download.Message);
        }

        // The re-listed row's manifest version is the built bundle's, which the pre-build row may not have known.
        if (!ExportBundles.CanRead(download.Bundle.ManifestVersion))
        {
            return ExportFetchResult.Unreadable(download.Bundle.ManifestVersion);
        }

        return _vault.CachedZip(download.Bundle) is { } zip
            ? ExportFetchResult.Ready(zip, download.Bundle.ManifestVersion)
            : ExportFetchResult.Failed(ExportReasons.DownloadFailed, download.Message);
    }
}
