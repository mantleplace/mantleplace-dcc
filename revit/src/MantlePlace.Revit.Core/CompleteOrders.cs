using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>What the vault window's Import does next for an order whose bundle is not on disk.</summary>
public enum ImportRoute
{
    /// <summary>The order is complete, or may be: fetch Revit's download view and import it.</summary>
    DownloadView,

    /// <summary>The order is built on demand and a build is running: the existing Prepare follows it.</summary>
    Prepare,

    /// <summary>The platform refused, and <see cref="ImportRouteDecision.Message"/> says why.</summary>
    Refused,
}

/// <summary>One materialize start, read as the route an Import press takes.</summary>
/// <param name="Route">Which way the import goes.</param>
/// <param name="Message">The refusal, for <see cref="ImportRoute.Refused"/>; empty otherwise.</param>
/// <param name="Delivered">
/// What the bundle already carries, from the no-op's delivered set. <c>null</c> when that is not
/// known — a <c>503 delivery_state_unknown</c> said nothing about it.
/// </param>
public readonly record struct ImportRouteDecision(
    ImportRoute Route,
    string Message,
    IReadOnlyList<string>? Delivered)
{
    public static ImportRouteDecision Refuse(string message) => new(ImportRoute.Refused, message, null);
}

/// <summary>
/// Complete orders and on-demand orders, told apart by one materialize start.
/// </summary>
/// <remarks>
/// <para>
/// The vault listing does not say whether an order was built complete at procurement or is built on
/// demand, so the plugin asks the one question whose answer differs: a materialize start. A complete
/// order has nothing left to build and answers the no-op; an on-demand order still being built
/// answers with a job, which is the existing Prepare's to follow. This is the interim answer: when
/// the listing carries the order's delivery model, it is read instead and the round trip goes.
/// </para>
/// <para>
/// ⛔ <b>A <c>503 delivery_state_unknown</c> is "try the download", not a refusal.</b> The platform
/// could not read what the bundle has, which it reports on complete orders too; refusing there
/// would tell a curator to retry an order that is ready. The download view answers the question
/// instead: its response carries a <c>view</c> block only for a bundle cut into parts, which is a
/// complete one (<see cref="HostViews"/>).
/// </para>
/// </remarks>
public static class CompleteOrders
{
    /// <summary>The status the platform answers when it could not read the bundle's delivery state.</summary>
    private const int ServiceUnavailable = 503;

    /// <summary>The single-flight status, which carries the running job and is read as a join.</summary>
    private const int Conflict = 409;

    /// <summary>The code beside that 503.</summary>
    public const string DeliveryStateUnknownCode = "delivery_state_unknown";

    /// <summary>
    /// Reads one materialize start — status and body — as the route an Import press takes.
    /// </summary>
    /// <remarks>
    /// The body is still read by shape (⛔<c>HPS-24</c>): a 409 goes to the parser, because the
    /// single-flight response is a join. Only the 503 is read by its code, and only to send it to the
    /// download rather than to the curator as an error.
    /// </remarks>
    public static ImportRouteDecision FromStart(int status, string body)
    {
        if (status == ServiceUnavailable
            && PlatformErrors.FromBody(body) is { } unknown
            && string.Equals(unknown.Code, DeliveryStateUnknownCode, StringComparison.Ordinal))
        {
            return new ImportRouteDecision(ImportRoute.DownloadView, string.Empty, null);
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

/// <summary>A minted download link for Revit's view, or for the whole archive when no view was cut.</summary>
/// <param name="Url">The link.</param>
/// <param name="ExpiresAt">When it stops working, as the platform wrote it.</param>
/// <param name="IsView">
/// Whether the platform cut a view. <c>false</c> means it answered with the whole archive — a bundle
/// stored as one zip, which is an on-demand order's — and the listing's size and digest describe it.
/// </param>
/// <param name="Partial">Whether the view was cut before the bundle completed, and so lacks parts.</param>
/// <param name="Parts">The parts the view holds (<c>&lt;token&gt;@&lt;level&gt;</c>, or a tokenless part).</param>
/// <param name="SizeBytes">The view zip's length, or <c>null</c> when the platform stated none.</param>
/// <param name="ManifestSha256">
/// The digest of the view's manifest member: the root the per-file digests are trusted from.
/// <c>null</c> when the platform stated none.
/// </param>
public readonly record struct HostViewLink(
    string Url,
    string ExpiresAt,
    bool IsView,
    bool Partial,
    IReadOnlyList<string> Parts,
    long? SizeBytes,
    string? ManifestSha256);

/// <summary>What to fetch, once the view link is in hand.</summary>
public enum ViewFetch
{
    /// <summary>Download the view: it holds every file Revit's import reads.</summary>
    View,

    /// <summary>The platform answered with the whole archive; download it, checked against the listing.</summary>
    WholeArchive,

    /// <summary>
    /// The view leaves out a file Revit reads from outside its host block; fetch the whole bundle
    /// instead, so nothing the import reads goes missing.
    /// </summary>
    WholeBundleInstead,

    /// <summary>The view is partial: the order is still being built, which is the Prepare's to follow.</summary>
    Prepare,
}

/// <summary>
/// Revit's download view: the request, the response, and whether the view is enough.
/// </summary>
/// <remarks>
/// <para>
/// A complete order's bundle is stored in parts, and the platform cuts a <b>host view</b> from them:
/// exactly the files the manifest's <c>hosts.revit</c> block points at, plus the whole-bundle
/// manifest with a <c>view</c> block. On the 2 km² Jackson reference bundle that is 87 MB of
/// 441 MB.
/// </para>
/// <para>
/// ⛔ <b>The host view holds only <c>hosts.revit</c>'s files, and Revit reads one file outside that
/// block: the tree points</b>, which the manifest's <c>layout</c> points at and which no host block
/// carries. A view without them would import with the trees missing, under a reason telling the
/// curator to download again — which fetches the same view. So the view is used only when it holds
/// every such file the order delivered; otherwise the whole bundle is fetched, as before. When the
/// platform puts the tree points in Revit's block, the view holds them and the fallback stops
/// firing on its own.
/// </para>
/// </remarks>
public static class HostViews
{
    /// <summary>
    /// The tokens whose files Revit's import reads from outside <c>hosts.revit</c>.
    /// </summary>
    /// <remarks>
    /// One entry today. The list grows only when a step that reads such a file lands, by the same
    /// rule as <see cref="MaterializeJobs.RevitTokens"/>; a file that moves into the host block
    /// leaves it.
    /// </remarks>
    public static IReadOnlyList<string> ReadOutsideHostBlock { get; } = ["landcover.tree_points_csv"];

    /// <summary>
    /// The level asked of every entry of the host block: <c>"*"</c> is "every entry not named".
    /// </summary>
    /// <remarks>
    /// The platform takes ONE level per entry, so "every level" is not a value it accepts. MAX is the
    /// one that holds them all today: a cut level (the road classes, the tree rows) is a subset of
    /// the MAX file, which a MAX view carries, and every other entry in Revit's block is MAX only. A
    /// level that is its own file would not come with MAX; none of Revit's entries has one.
    /// </remarks>
    public const string EveryEntryLevel = "MAX";

    /// <summary>The presign request body for Revit's view (platform route <c>POST …/download</c>).</summary>
    public static string BuildRequestBody()
        => "{\"format\":\"" + PresignedDownloads.WholeBundleFormat
            + "\",\"view\":{\"host\":\"" + BundleManifestReader.HostKey
            + "\",\"levels\":{\"*\":\"" + EveryEntryLevel + "\"}}}";

    /// <summary>Parses the view presign response. A body with no <c>url</c> is a refusal.</summary>
    /// <returns><c>null</c> on success.</returns>
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

    /// <summary>Decides what to fetch from the link the platform minted.</summary>
    /// <param name="link">The parsed response.</param>
    /// <param name="delivered">
    /// What the order delivered, from the no-op; <c>null</c> when unknown, and then every file read
    /// from outside the host block is required of the view.
    /// </param>
    public static ViewFetch Decide(HostViewLink link, IReadOnlyCollection<string>? delivered)
    {
        if (!link.IsView)
        {
            return ViewFetch.WholeArchive;
        }

        if (link.Partial)
        {
            return ViewFetch.Prepare;
        }

        return MissingFrom(link.Parts, delivered).Count == 0 ? ViewFetch.View : ViewFetch.WholeBundleInstead;
    }

    /// <summary>The tokens Revit reads from outside its block that the order has and the view lacks.</summary>
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

    /// <summary>The token a part holds: <c>&lt;token&gt;@&lt;level&gt;</c>, or a tokenless part's own id.</summary>
    public static string TokenOfPart(string part)
    {
        int at = (part ?? string.Empty).IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? part ?? string.Empty : part![..at];
    }
}
