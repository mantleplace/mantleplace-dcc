using System.Reflection;
using MantlePlace.PlatformApi;

namespace MantlePlace.Revit.Client;

/// <summary>
/// How this plugin reaches the Platform API: through the generated client, as <c>revit/&lt;plugin
/// version&gt;</c>, with one place to hear that the platform wants an update.
/// </summary>
/// <remarks>
/// <para>
/// Every Platform API call in this assembly — sign-in, refresh, the vault, the exports and the
/// Realtime route — is a <see cref="PlatformTransport"/> from <see cref="For"/>, so every request
/// carries <see cref="ClientHeader"/>. A suite case fails if a file here builds a route or a request
/// by hand.
/// </para>
/// <para>
/// <b>The version is this assembly's.</b> <c>revit/Directory.Build.props</c> is the plugin version's
/// one home and stamps it on all three assemblies, so the client's own version is the plugin's.
/// </para>
/// </remarks>
public static class PlatformApiCalls
{
    /// <summary>The client's name in the platform's list.</summary>
    public const string ClientName = "revit";

    /// <summary>The <c>Mantle-Place-Client</c> value every call carries.</summary>
    public static string ClientHeader { get; } = PlatformClientHeader.For(
        ClientName,
        typeof(PlatformApiCalls).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// Raised, on the calling thread, each time the platform answers 426 <c>client_update_required</c>.
    /// The add-in shows the update prompt; the refused call still reports the same sentence on its own
    /// channel (<see cref="Core.ClientUpdatePrompt"/>).
    /// </summary>
    public static event EventHandler<ClientUpdateNotice>? ClientUpdateRequired;

    /// <summary>A transport to the platform at <paramref name="baseUrl"/>.</summary>
    /// <param name="baseUrl">The platform's base URL.</param>
    /// <param name="handler">A message handler, for tests; the shared socket handler when <c>null</c>.</param>
    public static PlatformTransport For(string baseUrl, HttpMessageHandler? handler = null)
        => new(baseUrl, ClientHeader, handler, notice => ClientUpdateRequired?.Invoke(null, notice));

    /// <summary>
    /// The platform a configured full route URL points at: its scheme, host and port. The sign-in's
    /// grant URLs are configured whole (<see cref="MantlePlaceEndpoints.TokenEndpointUrl"/>), and the
    /// route itself is now the generated client's, so only the origin is read from them.
    /// </summary>
    public static string OriginOf(string routeUrl)
        => new Uri(routeUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
}
