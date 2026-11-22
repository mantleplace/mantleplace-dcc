using MantlePlace.PlatformApi;
using MantlePlace.Core;

namespace MantlePlace.Client;

public static class PlatformApiCalls
{
    public static string ClientName => HostIdentity.Id;

    public static string ClientHeader => PlatformClientHeader.For(ClientName, HostIdentity.PluginVersion);

    public static event EventHandler<ClientUpdateNotice>? ClientUpdateRequired;

    public static PlatformTransport For(string baseUrl, HttpMessageHandler? handler = null)
        => new(baseUrl, ClientHeader, handler, notice => ClientUpdateRequired?.Invoke(null, notice));

    public static string OriginOf(string routeUrl)
        => new Uri(routeUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
}
