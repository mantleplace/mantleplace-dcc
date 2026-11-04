namespace MantlePlace.Revit.Core;

/// <summary>
/// The line the import window shows once a bundle has imported, <i>Did this import come in right?</i>
/// with <b>Yes</b> and <b>No</b>, each a link to the web Feedback form (<c>HPS-56</c>).
/// </summary>
/// <remarks>
/// <para>
/// The plugin carries no feedback form and no feedback API: Yes and No are two addresses the shim opens
/// in the system browser, and opening one records nothing. The form shows the rating preselected and
/// stores it only when the curator presses send there.
/// </para>
/// <para>
/// This is the whole of the decision: which endings ask, which bundles can be asked about, and
/// the words. The window paints what it returns; <see cref="AskedBundles"/> is the memory that keeps a
/// bundle from being asked about twice.
/// </para>
/// </remarks>
public static class ImportFeedbackPrompt
{
    /// <summary>The question, in the standard's words.</summary>
    public const string Question = "Did this import come in right?";

    /// <summary>The answer that sends <see cref="FeedbackLink.RatingUp"/>.</summary>
    public const string Yes = "Yes";

    /// <summary>The answer that sends <see cref="FeedbackLink.RatingDown"/>.</summary>
    public const string No = "No";

    /// <summary>Sends nothing: the line goes, and the bundle is not asked about again.</summary>
    public const string Dismiss = "Dismiss";

    /// <summary>
    /// The bundle to ask about, or <c>null</c> when this import asks nothing.
    /// </summary>
    /// <remarks>
    /// Only an import that ran to its end asks: a failed, cancelled or dismissed one has no result for
    /// the curator to have an opinion about. Only a bundle with an order id that is a UUID asks, since
    /// an answer with no bundle to attach to is not worth the interruption. A bundle already asked
    /// about does not ask again.
    /// </remarks>
    /// <returns>The order id in its canonical form, which is also what <see cref="AskedBundles"/> remembers.</returns>
    public static string? Ask(ImportEnding ending, string? orderId, AskedBundles asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (ending != ImportEnding.Imported || FeedbackLink.OrderIdOf(orderId) is not { } order)
        {
            return null;
        }

        return asked.Contains(order) ? null : order;
    }

    /// <summary>The two addresses Yes and No open for <paramref name="orderId"/>.</summary>
    public static ImportFeedbackLinks Links(string? siteBaseUrl, string? pluginVersion, string? hostVersion, string orderId)
        => new(
            FeedbackLink.Build(siteBaseUrl, pluginVersion, hostVersion, orderId, FeedbackLink.RatingUp),
            FeedbackLink.Build(siteBaseUrl, pluginVersion, hostVersion, orderId, FeedbackLink.RatingDown));
}

/// <summary>What Yes and No open.</summary>
public sealed record ImportFeedbackLinks(string Yes, string No);
