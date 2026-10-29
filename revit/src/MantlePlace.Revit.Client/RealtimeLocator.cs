using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>Where the Realtime socket is and the key that opens it, or why neither is known.</summary>
/// <param name="Location">The Realtime base; empty when <paramref name="WhyNot"/> is set.</param>
/// <param name="PublishableKey">The publishable key; empty when <paramref name="WhyNot"/> is set. Never logged.</param>
/// <param name="WhyNot">The reason the connected host stays off; <c>null</c> when both values are known.</param>
public sealed record RealtimeValues(string Location, string PublishableKey, string? WhyNot);

/// <summary>
/// Resolves the Realtime location and key the connected host joins with: a local override first, then
/// the platform's published route.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order.</b> Either override set (<c>realtimeUrl</c> or <c>realtimePublishableKey</c> in
/// <c>config.json</c>) means the curator or a developer chose, and the route is not asked: a half-set
/// pair stays off with the message it always gave, because a configured location with the production
/// key is a guess at which key opens which socket. With neither set the route is read, and a pair is
/// taken whole or not at all.
/// </para>
/// <para>
/// <b>Off, with a reason.</b> A route that is unreachable, answers a non-success status, times out or
/// answers a body that does not carry both values leaves the host off, as an unconfigured install is
/// today. The reason comes back for the caller to log; the key is never in it.
/// </para>
/// <para>
/// The route is public and unauthenticated, so the request carries no credential of any kind.
/// </para>
/// </remarks>
public static class RealtimeLocator
{
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient SharedHttp = new();

    /// <summary>True when a local override leaves nothing to ask the platform.</summary>
    public static bool HasOverride(MantlePlaceEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return !string.IsNullOrWhiteSpace(endpoints.RealtimeUrl) || !string.IsNullOrWhiteSpace(endpoints.RealtimePublishableKey);
    }

    /// <summary>Resolves the values. Never throws for a network or a body; honours <paramref name="cancel"/>.</summary>
    /// <param name="endpoints">The loaded endpoints: the override, and the platform base to read the route from.</param>
    /// <param name="handler">For tests; the shared client when <c>null</c>.</param>
    /// <param name="cancel">Cancels the read.</param>
    public static async Task<RealtimeValues> ResolveAsync(
        MantlePlaceEndpoints endpoints,
        HttpMessageHandler? handler = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (HasOverride(endpoints))
        {
            string? why = RealtimeChannel.WhyOff(endpoints.RealtimeUrl, endpoints.RealtimePublishableKey);
            return why is null
                ? new RealtimeValues(endpoints.RealtimeUrl.Trim(), endpoints.RealtimePublishableKey.Trim(), null)
                : new RealtimeValues(string.Empty, string.Empty, why);
        }

        string route = endpoints.ApiBaseUrl.TrimEnd('/') + PublishedRealtime.Path;
        string body;
        try
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(RouteTimeout);
            using HttpClient? own = handler is null ? null : new HttpClient(handler, disposeHandler: false);
            HttpClient http = own ?? SharedHttp;
            using HttpRequestMessage request = new(HttpMethod.Get, route);
            using HttpResponseMessage response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Off($"the platform's Realtime route answered {(int)response.StatusCode}");
            }

            body = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Off("the platform's Realtime route did not answer in time");
        }
        catch (HttpRequestException ex)
        {
            return Off("the platform's Realtime route could not be reached (" + ex.Message + ")");
        }

        return PublishedRealtime.TryParse(body, out string location, out string key, out string whyNot)
            ? new RealtimeValues(location, key, null)
            : Off(whyNot);
    }

    private static RealtimeValues Off(string reason)
        => new(
            string.Empty,
            string.Empty,
            $"Connected host is off: {reason}, so this Revit joins no channel and tracks no presence. Send to it is unavailable; Download still works.");
}
