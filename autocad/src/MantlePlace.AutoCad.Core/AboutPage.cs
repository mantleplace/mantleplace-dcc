namespace MantlePlace.AutoCad;

/// <summary>What the About command says, assembled in the pure core so a test can hold it.</summary>
/// <remarks>
/// The sentences come from the shell's own readings (the build, the runtime, whether the Civil 3D part
/// is on, whether the session is signed in); this orders them and adds the one link About exists to
/// give, the privacy page on mantle.place. The window around them is the shell's.
/// </remarks>
public static class AboutPage
{
    /// <summary>The window's title.</summary>
    public const string Title = "About Mantle Place";

    /// <summary>The privacy page the About window links, on the site the plugin already talks to.</summary>
    public const string PrivacyPath = "/privacy";

    /// <summary>The privacy page's address under <paramref name="siteBaseUrl"/> (the deployment the plugin signs in against).</summary>
    /// <param name="siteBaseUrl">The site's base address, with or without a trailing slash.</param>
    public static string PrivacyUrl(string siteBaseUrl)
        => siteBaseUrl.TrimEnd('/') + PrivacyPath;

    /// <summary>The About body: the build line, then Civil 3D, then the session, one per paragraph.</summary>
    /// <param name="buildLine">Which Mantle Place build is loaded and which runtime it is on.</param>
    /// <param name="civil3dLine">Whether the Civil 3D part is on.</param>
    /// <param name="sessionSummary">The sentence the account face opens with: signed in as whom, or signed out.</param>
    public static string Body(string buildLine, string civil3dLine, string sessionSummary)
        => string.Join("\n\n", buildLine, civil3dLine, sessionSummary);
}
