using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The address <c>Send Feedback</c> and <c>Report a Problem</c> open in the system browser (<c>HPS-56</c>).
/// </summary>
/// <remarks>
/// <para>
/// The plugin carries no feedback form and no feedback API: the web app's Feedback page needs no
/// account and takes an optional image, so the one thing this host owes is a link that says where
/// the curator came from. The shim only opens what <see cref="Build"/> returns.
/// </para>
/// <para>
/// ⛔ <b>Nothing else goes in the URL.</b> The five parameters are the whole vocabulary: no address,
/// no name, no free text, no token. A version, order id or rating that fails its pattern is <b>left
/// out</b>, never sent raw, so a value that is not what it claims to be cannot ride along in the query.
/// </para>
/// </remarks>
public static class FeedbackLink
{
    /// <summary><c>dcc-</c> plus this host's directory name; matches <c>^[a-z0-9-]{1,40}$</c>.</summary>
    public const string Source = "dcc-revit";

    /// <summary>The site, when the configured address is unusable: a feedback link beats no link.</summary>
    public const string DefaultSite = "https://mantle.place";

    /// <summary>The <c>rating</c> the import prompt's Yes sends.</summary>
    public const string RatingUp = "up";

    /// <summary>The <c>rating</c> the import prompt's No sends.</summary>
    public const string RatingDown = "down";

    // \z, not $: .NET's $ also matches before a trailing newline, and "1.2\n" is not a version.
    private static readonly Regex VersionPattern = new(
        @"^[0-9]+(\.[0-9]+){0,3}\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The link for one action.
    /// </summary>
    /// <param name="siteBaseUrl">The deployment this plugin talks to; unusable falls back to <see cref="DefaultSite"/>.</param>
    /// <param name="pluginVersion">This add-in's version, digits and dots only (see <see cref="ReleaseVersion"/>).</param>
    /// <param name="hostVersion">The running Revit's version number — <c>2025</c>, <c>2026</c>, <c>2027</c>.</param>
    /// <param name="orderId">The bundle's order id, for <c>Report a Problem</c> and the import prompt only; omitted unless a UUID.</param>
    /// <param name="rating">The import prompt's answer, exactly <see cref="RatingUp"/> or <see cref="RatingDown"/>; anything else is omitted.</param>
    public static string Build(string? siteBaseUrl, string? pluginVersion, string? hostVersion, string? orderId = null, string? rating = null)
    {
        StringBuilder link = new();
        link.Append(AuthUrls.NormaliseBaseUrl(siteBaseUrl) ?? DefaultSite);
        link.Append("/feedback?source=").Append(Source);

        if (IsVersion(pluginVersion))
        {
            link.Append("&v=").Append(pluginVersion);
        }

        if (IsVersion(hostVersion))
        {
            link.Append("&hv=").Append(hostVersion);
        }

        if (OrderIdOf(orderId) is { } order)
        {
            link.Append("&order=").Append(order);
        }

        // Ordinal and exact: "UP", " up" and "up" plus a newline are not the token, and are not sent.
        if (rating is RatingUp or RatingDown)
        {
            link.Append("&rating=").Append(rating);
        }

        return link.ToString();
    }

    /// <summary>
    /// The order id in its canonical lower-case form, or <c>null</c> when it is not a UUID (or is the
    /// empty one): the one test of what may ride in <c>order</c>, and the key the import prompt
    /// remembers a bundle by.
    /// </summary>
    public static string? OrderIdOf(string? orderId)
        => orderId is not null
            && Guid.TryParseExact(orderId, "D", out Guid order)
            && order != Guid.Empty
                ? order.ToString("D", CultureInfo.InvariantCulture)
                : null;

    /// <summary>
    /// The version without the build metadata an informational version carries
    /// (<c>0.1.0+a36fcd8</c> becomes <c>0.1.0</c>): the commit sha is not part of the version and
    /// would fail the pattern, which would drop the whole value.
    /// </summary>
    public static string? ReleaseVersion(string? informationalVersion)
    {
        if (informationalVersion is null)
        {
            return null;
        }

        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    private static bool IsVersion(string? value) => value is not null && VersionPattern.IsMatch(value);
}
