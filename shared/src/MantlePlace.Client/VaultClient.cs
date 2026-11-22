using System.Net.Http;
using MantlePlace.PlatformApi;
using MantlePlace.Core;

namespace MantlePlace.Client;

public readonly record struct VaultResult<T>(T? Value, string? Error)
    where T : struct
{
    public static VaultResult<T> Ok(T value) => new(value, null);

    public static VaultResult<T> Failed(string error) => new(null, error);

    public bool Succeeded => Error is null;
}

public readonly record struct PollOutcome(VaultResult<MaterializeStatus> Result, bool OutOfPolls);

public readonly record struct CompleteDownload(
    bool Ready,
    bool NeedsPrepare,
    string Message,
    VaultBundle Bundle,
    ViewFetch Fetched);

public sealed class VaultClient
{
    private static readonly HttpClient Storage = new();

    private readonly MantlePlaceEndpoints _endpoints;
    private readonly AuthSession _session;
    private readonly HttpMessageHandler? _handler;
    private readonly IReadOnlyList<string> _threeFiles;

    public VaultClient(
        MantlePlaceEndpoints endpoints,
        AuthSession session,
        HttpMessageHandler? handler = null,
        IReadOnlyList<string>? threeFiles = null)
    {
        _endpoints = endpoints;
        _session = session;
        _handler = handler;
        _threeFiles = threeFiles ?? [];
    }

    public async Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken)
    {
        (int status, string body, string? error) = await SendAsync(
            (api, ct) => api.ListVaultBundlesAsync(ct), null, cancellationToken).ConfigureAwait(false);

        if (error is not null || Refusal(status, body) is { } refusal && (error = refusal) is not null)
        {
            return (null, error);
        }

        string? parseError = VaultListingReader.TryParse(body, out VaultListing listing);
        return parseError is null ? (listing, null) : (null, parseError);
    }

    public async Task<VaultResult<MaterializeStart>> StartMaterializeAsync(
        string orderId,
        string scope,
        CancellationToken cancellationToken)
    {
        if (!MaterializeJobs.IsValidScope(scope))
        {
            return VaultResult<MaterializeStart>.Failed(
                $"'{scope}' is not a scope this plugin offers. Use '{MaterializeJobs.HostScope}' or "
                + $"'{MaterializeJobs.AllScope}'.");
        }

        (int status, string body, string? error) = await SendAsync(
            (api, ct) => api.MaterializeVaultBundleFormatsAsync(orderId, new Body5(), ct),
            MaterializeJobs.BuildRequestBody(scope),
            cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return VaultResult<MaterializeStart>.Failed(error);
        }

        if (status != Conflict && Refusal(status, body) is { } refusal)
        {
            return VaultResult<MaterializeStart>.Failed(refusal);
        }

        string? parseError = MaterializeJobs.TryParseStart(body, out MaterializeStart start);
        return parseError is null
            ? VaultResult<MaterializeStart>.Ok(start)
            : VaultResult<MaterializeStart>.Failed(parseError);
    }

    public async Task<ImportRouteDecision> RouteImportAsync(string orderId, CancellationToken cancellationToken)
    {
        (int status, string body, string? error) = await SendAsync(
            (api, ct) => api.MaterializeVaultBundleFormatsAsync(orderId, new Body5(), ct),
            MaterializeJobs.BuildRequestBody(MaterializeJobs.HostScope),
            cancellationToken).ConfigureAwait(false);

        return error is not null ? ImportRouteDecision.Refuse(error) : CompleteOrders.FromStart(status, body);
    }

    public async Task<CompleteDownload> DownloadCompleteAsync(
        VaultBundle bundle,
        IReadOnlyCollection<string>? delivered,
        BundleCache cache,
        IProgress<string> say,
        CancellationToken cancellationToken,
        FidelityLevel? level = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(say);

        if (bundle.Levels is { } offer)
        {
            return await DownloadLevelAsync(bundle, level ?? offer.Recommended, cache, say, cancellationToken)
                .ConfigureAwait(false);
        }

        (int status, string body, string? error) = await SendAsync(
            (api, ct) => api.GetVaultBundleDownloadAsync(bundle.OrderId, new Body4(), ct),
            HostViews.BuildRequestBody(),
            cancellationToken).ConfigureAwait(false);

        HostViewLink link = default;
        error ??= Refusal(status, body) ?? HostViews.TryParse(body, out link);
        if (error is not null)
        {
            return new CompleteDownload(false, false, error, bundle, ViewFetch.View);
        }

        ViewFetch fetch = HostViews.Decide(link, delivered);

        switch (fetch)
        {
            case ViewFetch.Prepare:
                return new CompleteDownload(false, true, string.Empty, bundle, fetch);

            case ViewFetch.View:
            {
                if (IsExpired(link.ExpiresAt))
                {
                    return new CompleteDownload(false, false, ExpiredLink, bundle, fetch);
                }

                say.Report(link.SizeBytes is { } bytes
                    ? $"Downloading Revit's files ({bytes / 1_000_000.0:0} MB)…"
                    : "Downloading Revit's files…");

                string? viewError = await cache.PromoteAsync(
                    bundle.OrderId,
                    StreamFrom(link.Url),
                    link.SizeBytes,
                    expectedSha256: null,
                    bundle.ManifestVersion,
                    DateTimeOffset.UtcNow,
                    cancellationToken,
                    holdsView: true,
                    expectedManifestSha256: link.ManifestSha256).ConfigureAwait(false);

                return Settled(cache, bundle, viewError, fetch);
            }

            default:
            {
                say.Report("Fetching the integrity details…");
                (VaultListing? relisted, string? listError) = await ListAsync(cancellationToken).ConfigureAwait(false);
                if (listError is not null)
                {
                    return new CompleteDownload(false, false, listError, bundle, fetch);
                }

                VaultBundle refreshed = relisted!.Bundles
                    .FirstOrDefault(row => string.Equals(row.OrderId, bundle.OrderId, StringComparison.Ordinal))
                    ?? bundle;

                say.Report("Downloading…");
                string? wholeError;
                if (fetch == ViewFetch.WholeArchive)
                {
                    wholeError = IsExpired(link.ExpiresAt)
                        ? ExpiredLink
                        : await PromoteWholeAsync(refreshed, cache, link.Url, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    wholeError = await DownloadAsync(refreshed, cache, cancellationToken).ConfigureAwait(false);
                }

                return Settled(cache, refreshed, wholeError, fetch);
            }
        }
    }

    private async Task<CompleteDownload> DownloadLevelAsync(
        VaultBundle bundle,
        FidelityLevel level,
        BundleCache cache,
        IProgress<string> say,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> files = _threeFiles;
        int status;
        string body;
        string? error;
        while (true)
        {
            (status, body, error) = await SendAsync(
                (api, ct) => api.GetVaultBundleDownloadAsync(bundle.OrderId, new Body4(), ct),
                DownloadLevels.BuildRequestBody(level, files),
                cancellationToken).ConfigureAwait(false);

            if (error is not null || DownloadLevels.FilesLeftAfterNotInBundle(status, body, files) is not { } retry)
            {
                break;
            }

            say.Report($"This bundle holds no {string.Join(", ", retry.Absent)}; asking for the rest…");
            files = retry.Remaining;
        }

        HostViewLink link = default;
        error ??= Refusal(status, body) ?? HostViews.TryParse(body, out link);
        if (error is not null)
        {
            return new CompleteDownload(false, false, error, bundle, ViewFetch.View);
        }

        if (IsExpired(link.ExpiresAt))
        {
            return new CompleteDownload(false, false, ExpiredLink, bundle, ViewFetch.View);
        }

        string word = DownloadLevels.Word(level);
        say.Report(link.SizeBytes is { } bytes
            ? $"Downloading {word} ({VaultRows.SizeText(bytes)})…"
            : $"Downloading {word}…");

        string slot = DownloadLevels.SlotLevel(bundle, level);
        string? levelError = await cache.PromoteAsync(
            bundle.OrderId,
            StreamFrom(link.Url),
            link.SizeBytes,
            expectedSha256: null,
            bundle.ManifestVersion,
            DateTimeOffset.UtcNow,
            cancellationToken,
            holdsView: true,
            expectedManifestSha256: link.ManifestSha256,
            level: slot).ConfigureAwait(false);

        return Settled(cache, bundle, levelError, ViewFetch.View, slot);
    }

    private const string ExpiredLink = "The download link expired before it could be used. Try again.";

    private static bool IsExpired(string expiresAt)
        => new PresignedDownload(string.Empty, expiresAt).IsExpired(DateTimeOffset.UtcNow);

    private static CompleteDownload Settled(
        BundleCache cache,
        VaultBundle bundle,
        string? error,
        ViewFetch fetch,
        string level = BundleCache.HighestLevel)
    {
        CacheEntry entry = cache.InspectQuick(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion, level);
        return new CompleteDownload(
            error is null && entry.State == CacheState.CachedValid,
            false,
            error ?? entry.Describe(),
            bundle,
            fetch);
    }

    public async Task<VaultResult<MaterializeStatus>> PollOnceAsync(
        string orderId,
        IReadOnlyCollection<string> requested,
        CancellationToken cancellationToken)
    {
        (int status_, string body, string? error) = await SendAsync(
            (api, ct) => api.GetVaultBundleMaterializeStatusAsync(orderId, ct), null, cancellationToken).ConfigureAwait(false);

        if (error is not null || Refusal(status_, body) is { } refusal && (error = refusal) is not null)
        {
            return VaultResult<MaterializeStatus>.Failed(error);
        }

        string? parseError = MaterializeJobs.TryParseStatus(body, requested, out MaterializeStatus status);
        return parseError is null
            ? VaultResult<MaterializeStatus>.Ok(status)
            : VaultResult<MaterializeStatus>.Failed(parseError);
    }

    public async Task<PollOutcome> PollToCompletionAsync(
        string orderId,
        IReadOnlyCollection<string> requested,
        IProgress<MaterializeStatus>? progress,
        CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        string lastError = "The platform stopped responding while building this bundle.";

        for (int poll = 0; poll < MaterializeJobs.MaxPolls; poll++)
        {
            VaultResult<MaterializeStatus> result = await PollOnceAsync(orderId, requested, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                lastError = result.Error!;
                if (++consecutiveFailures >= MaterializeJobs.MaxConsecutivePollFailures)
                {
                    return new PollOutcome(VaultResult<MaterializeStatus>.Failed(lastError), OutOfPolls: false);
                }
            }
            else
            {
                consecutiveFailures = 0;
                MaterializeStatus status = result.Value!.Value;
                progress?.Report(status);

                switch (status.State)
                {
                    case MaterializeState.Complete:
                        return new PollOutcome(VaultResult<MaterializeStatus>.Ok(status), OutOfPolls: false);
                    case MaterializeState.Failed:
                        return new PollOutcome(
                            VaultResult<MaterializeStatus>.Failed(
                                status.Message.Length > 0
                                    ? status.Message
                                    : "The platform could not build this bundle."),
                            OutOfPolls: false);
                    default:
                        break;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(MaterializeJobs.PollIntervalSeconds), cancellationToken)
                .ConfigureAwait(false);
        }

        return new PollOutcome(
            VaultResult<MaterializeStatus>.Failed(
                "This bundle is taking longer than expected to build. It is still running — press "
                + $"“{WindowLabels.PrepareForRevit}” again in a few minutes to pick up where it left off."),
            OutOfPolls: true);
    }

    public async Task<VaultResult<PresignedDownload>> PresignAsync(string orderId, CancellationToken cancellationToken)
    {
        (int status, string body, string? error) = await SendAsync(
            (api, ct) => api.GetVaultBundleDownloadAsync(orderId, new Body4(), ct),
            PresignedDownloads.BuildRequestBody(),
            cancellationToken).ConfigureAwait(false);

        if (error is null && Refusal(status, body) is { } refusal)
        {
            error = refusal;
        }

        if (error is not null)
        {
            return VaultResult<PresignedDownload>.Failed(error);
        }

        string? parseError = PresignedDownloads.TryParse(body!, out PresignedDownload download);
        return parseError is null
            ? VaultResult<PresignedDownload>.Ok(download)
            : VaultResult<PresignedDownload>.Failed(parseError);
    }

    public async Task<string?> DownloadAsync(
        VaultBundle bundle,
        BundleCache cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(cache);

        VaultResult<PresignedDownload> presign = await PresignAsync(bundle.OrderId, cancellationToken)
            .ConfigureAwait(false);

        if (!presign.Succeeded)
        {
            return presign.Error;
        }

        PresignedDownload link = presign.Value!.Value;
        if (link.IsExpired(DateTimeOffset.UtcNow))
        {
            return "The download link expired before it could be used. Try again.";
        }

        return await PromoteWholeAsync(bundle, cache, link.Url, cancellationToken).ConfigureAwait(false);
    }

    private static Task<string?> PromoteWholeAsync(
        VaultBundle bundle,
        BundleCache cache,
        string url,
        CancellationToken cancellationToken)
        => cache.PromoteAsync(
            bundle.OrderId,
            StreamFrom(url),
            bundle.SizeBytes,
            bundle.Sha256,
            bundle.ManifestVersion,
            DateTimeOffset.UtcNow,
            cancellationToken);

    private static Func<Stream, CancellationToken, Task> StreamFrom(string url)
        => async (destination, token) =>
        {
            using HttpResponseMessage response = await Storage
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
        };

    private const int Conflict = 409;

    private string Base() => AuthUrls.NormaliseBaseUrl(_endpoints.ApiBaseUrl)
        ?? throw new InvalidOperationException(
            $"'{_endpoints.ApiBaseUrl}' is not a usable API base URL. Fix apiBaseUrl in "
            + MantlePlaceEndpoints.ConfigPath + ".");

    private async Task<(int Status, string Body, string? Error)> SendAsync(
        Func<PlatformApiClient, CancellationToken, Task> operation,
        string? jsonBody,
        CancellationToken cancellationToken,
        bool isRetry = false)
    {
        string accessToken = _session.AccessToken;
        if (accessToken.Length == 0)
        {
            if (!isRetry && _session.CanRenewSession
                && (await _session.RefreshAsync(cancellationToken).ConfigureAwait(false)).Succeeded)
            {
                return await SendAsync(operation, jsonBody, cancellationToken, isRetry: true)
                    .ConfigureAwait(false);
            }

            return (0, string.Empty, "Sign in to Mantle Place first.");
        }

        try
        {
            (int status, string body) = await PlatformApiCalls.For(Base(), _handler)
                .SendAsync(operation, accessToken, cancellationToken, jsonBody)
                .ConfigureAwait(false);

            if (status == 401 && !isRetry && _session.CanRenewSession
                && (await _session.RefreshAsync(cancellationToken).ConfigureAwait(false)).Succeeded)
            {
                return await SendAsync(operation, jsonBody, cancellationToken, isRetry: true)
                    .ConfigureAwait(false);
            }

            return (status, body, null);
        }
        catch (HttpRequestException ex)
        {
            return (0, string.Empty, _endpoints.Unreachable(Base(), ex.Message));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (0, string.Empty, "The request to mantle.place timed out.");
        }
    }

    private static string? Refusal(int status, string body) => PlatformErrors.Refusal(status, body);
}
