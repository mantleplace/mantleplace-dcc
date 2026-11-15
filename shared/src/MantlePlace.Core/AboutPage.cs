namespace MantlePlace.Core;

/// <summary>What the About window says, assembled in the pure core so a test can hold it.</summary>
/// <remarks>
/// The sentences come from the host shell's own readings (the build, the host it is running in, whether
/// a part is on, whether the session is signed in); this names the window, orders them and adds the one
/// link About exists to give, the privacy page on mantle.place. The window around them is the shared
/// <c>AboutWindow</c>, which both hosts open.
/// </remarks>
public static class AboutPage
{
    /// <summary>The window's title: the shared action's words (<c>HPS-51</c>).</summary>
    public const string Title = "About Mantle Place";

    /// <summary>The privacy page the About window links, on the site the plugin already talks to.</summary>
    public const string PrivacyPath = "/privacy";

    /// <summary>The heading beside the mark: the product, in the host it is running in.</summary>
    /// <param name="hostName">The host's product name (<see cref="WindowLabels.HostName"/>).</param>
    public static string Heading(string hostName) => $"Mantle Place for {hostName}";

    /// <summary>The privacy page's address under <paramref name="siteBaseUrl"/> (the deployment the plugin signs in against).</summary>
    /// <param name="siteBaseUrl">The site's base address, with or without a trailing slash.</param>
    public static string PrivacyUrl(string siteBaseUrl)
        => siteBaseUrl.TrimEnd('/') + PrivacyPath;

    /// <summary>
    /// The About body: the host's paragraphs, one per paragraph, in the order given. AutoCAD gives the
    /// build, then Civil 3D, then the session; Revit the build, the Revit it is running in, the versions
    /// it supports, then the session.
    /// </summary>
    /// <param name="paragraphs">The host's sentences, each a paragraph.</param>
    public static string Body(params string[] paragraphs)
        => string.Join("\n\n", paragraphs);
}
