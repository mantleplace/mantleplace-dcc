namespace MantlePlace.Core;

public static class ImportFeedbackPrompt
{
    public const string Question = "Did this import come in right?";

    public const string Yes = "Yes";

    public const string No = "No";

    public const string Dismiss = "Dismiss";

    public static string? Ask(ImportEnding ending, string? orderId, AskedBundles asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (ending != ImportEnding.Imported || FeedbackLink.OrderIdOf(orderId) is not { } order)
        {
            return null;
        }

        return asked.Contains(order) ? null : order;
    }

    public static ImportFeedbackLinks Links(string? siteBaseUrl, string? pluginVersion, string? hostVersion, string orderId)
        => new(
            FeedbackLink.Build(siteBaseUrl, pluginVersion, hostVersion, orderId, FeedbackLink.RatingUp),
            FeedbackLink.Build(siteBaseUrl, pluginVersion, hostVersion, orderId, FeedbackLink.RatingDown));
}

public sealed record ImportFeedbackLinks(string Yes, string No);
