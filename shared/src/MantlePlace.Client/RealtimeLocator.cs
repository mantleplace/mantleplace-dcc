using MantlePlace.PlatformApi;
using MantlePlace.Core;

namespace MantlePlace.Client;

public sealed record RealtimeValues(string Location, string PublishableKey, string? WhyNot);

public static class RealtimeLocator
{
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(10);

    public static bool HasOverride(MantlePlaceEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return !string.IsNullOrWhiteSpace(endpoints.RealtimeUrl) || !string.IsNullOrWhiteSpace(endpoints.RealtimePublishableKey);
    }

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

        string body;
        try
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(RouteTimeout);
            (int status, body) = await PlatformApiCalls.For(endpoints.ApiBaseUrl.TrimEnd('/'), handler)
                .SendAsync((api, ct) => api.GetRealtimeConfigAsync(ct), null, limit.Token)
                .ConfigureAwait(false);
            if (status is < 200 or >= 300)
            {
                return Off($"the platform's Realtime route answered {status}");
            }
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
