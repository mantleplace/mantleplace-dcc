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
        (int status, string body, string? error) = await SendAsync(
            HttpMethod.Get, BundlesUrl(), null, cancellationToken).ConfigureAwait(false);

        if (error is not null || Refusal(status, body) is { } refusal && (error = refusal) is not null)
        {
            return (null, error);
        }

        string? parseError = VaultListingReader.TryParse(body, out VaultListing listing);
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

        (int status, string body, string? error) = await SendAsync(
            HttpMethod.Post,
            MaterializeUrl(orderId),
            MaterializeJobs.BuildRequestBody(scope),
            cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            return VaultResult<MaterializeStart>.Failed(error);
        }

        // ⛔HPS-24: the start response is recognised by BODY SHAPE, not by status code, so the
        // single-flight 409 goes to the PARSER — its body carries the running job, and refusing it
        // here would tell a curator to retry a run that is already going. This exception is the
        // whole reason `Refusal` is a separate call rather than something SendAsync applies: the
        // Unreal host makes the identical one, and the two must not drift.
        if (status != Conflict && Refusal(status, body) is { } refusal)
        {
            return VaultResult<MaterializeStart>.Failed(refusal);
        }

        string? parseError = MaterializeJobs.TryParseStart(body, out MaterializeStart start);
        return parseError is null
            ? VaultResult<MaterializeStart>.Ok(start)
            : VaultResult<MaterializeStart>.Failed(parseError);
    }

    /// <summary>One poll.</summary>
    /// <param name="requested">
    /// The tokens whose delivery decides completion. The platform answers this endpoint with a
    /// delivery-state document carrying no status word, so without this there is nothing to compare
    /// against and no way to know the job is done.
    /// </param>
    public async Task<VaultResult<MaterializeStatus>> PollOnceAsync(
        string orderId,
        IReadOnlyCollection<string> requested,
        CancellationToken cancellationToken)
    {
        (int status_, string body, string? error) = await SendAsync(
            HttpMethod.Get, MaterializeUrl(orderId), null, cancellationToken).ConfigureAwait(false);

        if (error is not null || Refusal(status_, body) is { } refusal && (error = refusal) is not null)
        {
            return VaultResult<MaterializeStatus>.Failed(error);
        }

        string? parseError = MaterializeJobs.TryParseStatus(body, requested, out MaterializeStatus status);
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
        (int status, string body, string? error) = await SendAsync(
            HttpMethod.Post,
            DownloadUrl(orderId),
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

    /// <summary>The single-flight status: a refusal everywhere except the materialize start.</summary>
    private const int Conflict = 409;

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
    /// One request, with the bearer token attached. Reports the status code alongside the body so
    /// the caller decides what a non-2xx means.
    /// </summary>
    /// <remarks>
    /// <c>Error</c> is set only when there was no response at all. Whether a response that arrived
    /// is a refusal is <see cref="Refusal"/>'s question, because one caller — the materialize start —
    /// legitimately reads a 409 as a success (<c>HPS-24</c>).
    /// </remarks>
    private async Task<(int Status, string Body, string? Error)> SendAsync(
        HttpMethod method,
        string url,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        string accessToken = _session.AccessToken;
        if (accessToken.Length == 0)
        {
            return (0, string.Empty, "Sign in to Mantle Place first.");
        }

        using HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return ((int)response.StatusCode, body, null);
        }
        catch (HttpRequestException ex)
        {
            return (0, string.Empty, $"Could not reach mantle.place: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (0, string.Empty, "The request to mantle.place timed out.");
        }
    }

    /// <summary>
    /// A non-2xx reduced to something a curator can act on, or <c>null</c> when the response was a
    /// success (<c>HPS-48</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A non-2xx whose body explains itself is returned as that explanation. The status code alone
    /// ("410") tells a curator nothing; "This order was refunded" tells them everything.
    /// </para>
    /// <para>
    /// It is returned as <see cref="PlatformError.Sentence"/>, not <c>Message</c>: this is the one
    /// read with no parser behind it to add context, so a schema rejection that answers
    /// "Invalid request" and names the offending field in a sibling must show both halves. Showing
    /// only the first is how a missing request field looked like an unexplained refusal.
    /// </para>
    /// <para>
    /// ⛔ <b>A non-2xx that explains nothing is still a refusal.</b> Passing its body through as
    /// success is how a 502 proxy page and a 500 with an unfamiliar error envelope both reached a
    /// parser expecting a materialize response, and surfaced as "the platform accepted the request
    /// but named no job to poll" — blaming the shape of a body that was never a success in the first
    /// place.
    /// </para>
    /// </remarks>
    private static string? Refusal(int status, string body)
        => status is >= 200 and < 300
            ? null
            : PlatformErrors.FromBody(body) is { } platformError
                ? platformError.Sentence
                : $"mantle.place refused this request (HTTP {status}) and gave no reason.";
}
