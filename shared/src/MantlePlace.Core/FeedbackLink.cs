using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MantlePlace.Core;

public static class FeedbackLink
{
    public static string Source => "dcc-" + HostIdentity.Id;

    public const string DefaultSite = "https://mantle.place";

    public const string RatingUp = "up";

    public const string RatingDown = "down";

    private static readonly Regex VersionPattern = new(
        @"^[0-9]+(\.[0-9]+){0,3}\z",
        RegexOptions.CultureInvariant);

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

        if (rating is RatingUp or RatingDown)
        {
            link.Append("&rating=").Append(rating);
        }

        return link.ToString();
    }

    public static string? OrderIdOf(string? orderId)
        => orderId is not null
            && Guid.TryParseExact(orderId, "D", out Guid order)
            && order != Guid.Empty
                ? order.ToString("D", CultureInfo.InvariantCulture)
                : null;

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
