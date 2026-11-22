namespace MantlePlace.Core;

public static class AboutPage
{
    public const string Title = "About Mantle Place";

    public const string PrivacyPath = "/privacy";

    public static string Heading(string hostName) => $"Mantle Place for {hostName}";

    public static string PrivacyUrl(string siteBaseUrl)
        => siteBaseUrl.TrimEnd('/') + PrivacyPath;

    public static string Body(params string[] paragraphs)
        => string.Join("\n\n", paragraphs);
}
