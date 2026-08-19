using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>The result of a vault call: one of the two is always null.</summary>
public readonly record struct VaultResult<T>(T? Value, string? Error)
    where T : struct
{
    public static VaultResult<T> Ok(T value) => new(value, null);

    public static VaultResult<T> Failed(string error) => new(null, error);

    public bool Succeeded => Error is null;
}

/// <summary>
/// The vault surface: list → materialize → poll → re-list → presign → download (<c>HPS-18</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The re-list after materialize is not optional.</b> It is where the host obtains the integrity
/// facts — sha256, size, manifest version — that the freshly built bundle now has and the
/// pre-materialize listing did not. Skipping it means downloading a bundle whose expected digest
/// nobody knows and reporting it valid-but-unverified forever.
/// </para>
/// <para>
/// Every parse this makes lives in <c>MantlePlace.Revit.Core</c>; what is here is HTTP and the
/// polling loop.
/// </para>
/// </remarks>
public sealed class VaultClient
{
    private static readonly HttpClient Http = new();

    private readonly MantlePlaceEndpoints _endpoints;
    private readonly AuthSession _session;

    public VaultClient(MantlePlaceEndpoints endpoints, AuthSession session)
    {
        _endpoints = endpoints;
        _session = session;
    }

    /// <summary>Lists the curator's bundles.</summary>
    public async Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken)
    {
        (string? body, string? error) = await SendAsync(HttpMethod.Get, BundlesUrl(), null, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return (null, error);
        }

        string? parseError = VaultListingReader.TryParse(body!, out VaultListing listing);
        return parseError is null ? (listing, null) : (null, parseError);
    }

    /// <summary>
    /// Starts — or joins — a materialize for one order.
    /// </summary>
    /// <remarks>
    /// ⛔<c>HPS-23</c>: the body carries this host's explicit token list, never a scope keyword the
    /// server would expand to its own idea of what Revit wants.
    /// </remarks>
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

        (string? body, string? error) = await SendAsync(
            HttpMethod.Post,
            MaterializeUrl(orderId),
            MaterializeJobs.BuildRequestBody(scope),
            cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return VaultResult<MaterializeStart>.Failed(error);
        }

        string? parseError = MaterializeJobs.TryParseStart(body!, out MaterializeStart start);
        return parseError is null
            ? VaultResult<MaterializeStart>.Ok(start)
            : VaultResult<MaterializeStart>.Failed(parseError);
    }

    /// <summary>One poll.</summary>
    public async Task<VaultResult<MaterializeStatus>> PollOnceAsync(
        string orderId,
        CancellationToken cancellationToken)
    {
        (string? body, string? error) = await SendAsync(HttpMethod.Get, MaterializeUrl(orderId), null, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return VaultResult<MaterializeStatus>.Failed(error);
        }

        string? parseError = MaterializeJobs.TryParseStatus(body!, out MaterializeStatus status);
        return parseError is null
            ? VaultResult<MaterializeStatus>.Ok(status)
            : VaultResult<MaterializeStatus>.Failed(parseError);
    }

    /// <summary>
    /// Polls to a terminal state, bounded on the three axes <c>HPS-25</c> declares.
    /// </summary>
    /// <remarks>
    /// The failure cap is CONSECUTIVE. A job that survives scattered transient errors over ten
    /// minutes is healthy, and abandoning it would discard an ETL run the curator paid for.
    /// Abandoning the poll does not abandon the JOB either — reopening the panel rejoins it through
    /// <c>HPS-24</c>.
    /// </remarks>
    public async Task<VaultResult<MaterializeStatus>> PollToCompletionAsync(
        string orderId,
        IProgress<MaterializeStatus>? progress,
        CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        string lastError = "The platform stopped responding while building this bundle.";

        for (int poll = 0; poll < MaterializeJobs.MaxPolls; poll++)
        {
            VaultResult<MaterializeStatus> result = await PollOnceAsync(orderId, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                lastError = result.Error!;
                if (++consecutiveFailures >= MaterializeJobs.MaxConsecutivePollFailures)
                {
                    return VaultResult<MaterializeStatus>.Failed(lastError);
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
                        return VaultResult<MaterializeStatus>.Ok(status);
                    case MaterializeState.Failed:
                        return VaultResult<MaterializeStatus>.Failed(
                            status.Message.Length > 0
                                ? status.Message
                                : "The platform could not build this bundle.");
                    default:
                        break;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(MaterializeJobs.PollIntervalSeconds), cancellationToken)
                .ConfigureAwait(false);
        }

        return VaultResult<MaterializeStatus>.Failed(
            "This bundle is taking longer than expected to build. It is still running — reopen the vault "
            + "in a few minutes and it will pick up where it left off.");
    }

    /// <summary>Mints a presigned URL. Per import, never cached (<c>HPS-29</c>).</summary>
    public async Task<VaultResult<PresignedDownload>> PresignAsync(string orderId, CancellationToken cancellationToken)
    {
        (string? body, string? error) = await SendAsync(HttpMethod.Post, DownloadUrl(orderId), "{}", cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
        {
            return VaultResult<PresignedDownload>.Failed(error);
        }

        string? parseError = PresignedDownloads.TryParse(body!, out PresignedDownload download);
        return parseError is null
            ? VaultResult<PresignedDownload>.Ok(download)
            : VaultResult<PresignedDownload>.Failed(parseError);
    }

    /// <summary>
    /// Presigns, streams to the cache's <c>.part</c>, verifies and promotes (⛔<c>HPS-26</c>).
    /// </summary>
    /// <returns><c>null</c> on success.</returns>
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

        return await cache.PromoteAsync(
            bundle.OrderId,
            async (destination, token) =>
            {
                using HttpResponseMessage response = await Http
                    .GetAsync(link.Url, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
            },
            bundle.SizeBytes,
            bundle.Sha256,
            bundle.ManifestVersion,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
    }

    private string BundlesUrl() => Base() + "/api/v1/vault/bundles";

    private string MaterializeUrl(string orderId)
        => Base() + "/api/v1/vault/bundles/" + AuthUrls.PercentEncode(orderId) + "/materialize";

    private string DownloadUrl(string orderId)
        => Base() + "/api/v1/vault/bundles/" + AuthUrls.PercentEncode(orderId) + "/download";

    /// <summary>
    /// <c>HPS-19</c>: trimmed, trailing slashes stripped, and validated to have a host before use.
    /// </summary>
    private string Base() => AuthUrls.NormaliseBaseUrl(_endpoints.ApiBaseUrl)
        ?? throw new InvalidOperationException(
            $"'{_endpoints.ApiBaseUrl}' is not a usable API base URL. Fix apiBaseUrl in "
            + MantlePlaceEndpoints.ConfigPath + ".");

    /// <summary>
    /// One request, with the bearer token attached and the platform's own error text preferred over
    /// anything this client could invent (<c>HPS-48</c>).
    /// </summary>
    private async Task<(string? Body, string? Error)> SendAsync(
        HttpMethod method,
        string url,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        string accessToken = _session.AccessToken;
        if (accessToken.Length == 0)
        {
            return (null, "Sign in to Mantle Place first.");
        }

        using HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // A non-2xx whose body explains itself is returned as that explanation. The status code
            // alone ("410") tells a curator nothing; "This order was refunded" tells them everything.
            if (!response.IsSuccessStatusCode && PlatformErrors.FromBody(body) is { } platformError)
            {
                return (null, platformError.Message);
            }

            return (body, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Could not reach mantle.place: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The request to mantle.place timed out.");
        }
    }
}
