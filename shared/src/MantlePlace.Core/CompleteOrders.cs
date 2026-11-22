using System.Text.Json;

namespace MantlePlace.Core;

public enum ImportRoute
{
    DownloadView,

    Prepare,

    Refused,
}

public readonly record struct ImportRouteDecision(
    ImportRoute Route,
    string Message,
    IReadOnlyList<string>? Delivered)
{
    public static ImportRouteDecision Refuse(string message) => new(ImportRoute.Refused, message, null);
}

public static class CompleteOrders
{
    private const int ServiceUnavailable = 503;

    private const int Conflict = 409;

    public const string DeliveryStateUnknownCode = "delivery_state_unknown";

    private const int BadRequest = 400;

    public const string LegacyOrderCode = "legacy_order";

    public static ImportRouteDecision FromStart(int status, string body)
    {
        if (status == ServiceUnavailable
            && PlatformErrors.FromBody(body) is { } unknown
            && string.Equals(unknown.Code, DeliveryStateUnknownCode, StringComparison.Ordinal))
        {
            return new ImportRouteDecision(ImportRoute.DownloadView, string.Empty, null);
        }

        if (status == BadRequest
            && PlatformErrors.FromBody(body) is { } legacy
            && string.Equals(legacy.Code, LegacyOrderCode, StringComparison.Ordinal))
        {
            return new ImportRouteDecision(ImportRoute.DownloadView, string.Empty, HostViews.ReadOutsideHostBlock);
        }

        if (status != Conflict && PlatformErrors.Refusal(status, body) is { } refusal)
        {
            return ImportRouteDecision.Refuse(refusal);
        }

        if (MaterializeJobs.TryParseStart(body, out MaterializeStart start) is { } parseError)
        {
            return ImportRouteDecision.Refuse(parseError);
        }

        return start.Outcome == MaterializeStartOutcome.NothingToDo
            ? new ImportRouteDecision(ImportRoute.DownloadView, string.Empty, start.Tokens)
            : new ImportRouteDecision(ImportRoute.Prepare, string.Empty, null);
    }
}

public readonly record struct HostViewLink(
    string Url,
    string ExpiresAt,
    bool IsView,
    bool Partial,
    IReadOnlyList<string> Parts,
    long? SizeBytes,
    string? ManifestSha256);

public enum ViewFetch
{
    View,

    WholeArchive,

    WholeBundleInstead,

    Prepare,
}

public static class HostViews
{
    public static IReadOnlyList<string> ReadOutsideHostBlock
        => HostIdentity.Id == RevitHostId ? RevitReadsOutsideHostBlock : [];

    private const string RevitHostId = "revit";

    private static readonly string[] RevitReadsOutsideHostBlock = ["landcover.tree_points_csv"];

    public const string EveryEntryLevel = "MAX";

    public static string BuildRequestBody()
        => "{\"format\":\"" + PresignedDownloads.WholeBundleFormat
            + "\",\"view\":{\"host\":\"" + HostIdentity.Id
            + "\",\"levels\":{\"*\":\"" + EveryEntryLevel + "\"}}}";

    public static string? TryParse(string body, out HostViewLink link)
    {
        link = new HostViewLink(string.Empty, string.Empty, false, false, [], null, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body ?? string.Empty);
        }
        catch (JsonException)
        {
            return "The download response was not valid JSON.";
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "The download response was not valid JSON.";
            }

            string url = root.Str("url");
            if (url.Length == 0)
            {
                return PlatformErrors.TryRead(root, out PlatformError error)
                    ? error.Message
                    : "The platform returned no download link for this bundle.";
            }

            JsonElement? view = root.Object("view");
            double? size = root.OptionalDouble("sizeBytes");

            link = new HostViewLink(
                url,
                root.Str("expiresAt"),
                view is not null,
                view?.Bool("partial") ?? false,
                view?.StringArray("parts") ?? [],
                size is { } bytes ? (long)bytes : null,
                root.OptionalStr("manifestSha256"));
            return null;
        }
    }

    public static ViewFetch Decide(HostViewLink link, IReadOnlyCollection<string>? delivered)
    {
        if (!link.IsView)
        {
            return delivered is null ? ViewFetch.Prepare : ViewFetch.WholeArchive;
        }

        if (link.Partial)
        {
            return ViewFetch.Prepare;
        }

        return MissingFrom(link.Parts, delivered).Count == 0 ? ViewFetch.View : ViewFetch.WholeBundleInstead;
    }

    public static IReadOnlyList<string> MissingFrom(
        IReadOnlyCollection<string> parts,
        IReadOnlyCollection<string>? delivered)
    {
        ArgumentNullException.ThrowIfNull(parts);

        HashSet<string> held = new(parts.Select(TokenOfPart), StringComparer.Ordinal);
        return
        [
            .. ReadOutsideHostBlock.Where(token =>
                (delivered is null || delivered.Contains(token)) && !held.Contains(token)),
        ];
    }

    public static string TokenOfPart(string part)
    {
        int at = (part ?? string.Empty).IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? part ?? string.Empty : part![..at];
    }
}
