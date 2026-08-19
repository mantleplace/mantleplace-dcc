using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>Where a materialize job stands, as an open vocabulary (<c>HPS-22</c>).</summary>
public enum MaterializeState
{
    Unknown,
    Pending,
    Processing,
    Complete,
    Failed,
}

/// <summary>The response to starting a materialize.</summary>
/// <param name="JobId">The job to poll.</param>
/// <param name="AlreadyRunning">
/// True when this joined an in-flight job rather than starting one (<c>HPS-24</c>).
/// </param>
public readonly record struct MaterializeStart(string JobId, bool AlreadyRunning);

/// <summary>One poll of a materialize job.</summary>
/// <param name="State">Where it stands.</param>
/// <param name="Fraction">
/// Progress in <c>[0, 1]</c>, or <b>-1 for indeterminate</b>. A progress bar sitting at 0% and a
/// spinner say different things to a curator deciding whether to wait.
/// </param>
/// <param name="JobId">Echoed when the body carries it.</param>
/// <param name="Message">The platform's reason, when a job failed.</param>
public readonly record struct MaterializeStatus(MaterializeState State, double Fraction, string JobId, string Message)
{
    /// <summary>What an absent progress value means. Not zero.</summary>
    public const double Indeterminate = -1.0;
}

/// <summary>Starting, joining and polling a materialize job (<c>HPS-23</c> … <c>HPS-25</c>).</summary>
public static class MaterializeJobs
{
    /// <summary>Seconds between polls. The reference floor; going below it is rate-limit bait.</summary>
    public const double PollIntervalSeconds = 3.0;

    /// <summary>Polls before giving up — 200 × 3 s ≈ 10 minutes.</summary>
    public const int MaxPolls = 200;

    /// <summary>
    /// CONSECUTIVE poll failures tolerated before abandoning.
    /// </summary>
    /// <remarks>
    /// Consecutive, not cumulative: a job that survives ten scattered transient errors over ten
    /// minutes is healthy, and abandoning it would discard a completed ETL run the curator paid for.
    /// </remarks>
    public const int MaxConsecutivePollFailures = 5;

    /// <summary>
    /// ⛔<c>HPS-23</c>: the explicit, client-owned token list this host asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never the server-side scope keyword. The platform expands a host-name keyword to its own idea
    /// of that host's core set, which is how the reference host silently lost its vector and
    /// landcover layers. The plugin owns which layers it needs, so the plugin enumerates them.
    /// </para>
    /// <para>
    /// Eight tokens, trimmed to what the importer actually reaches for: the two topo paths, the site
    /// model, the two "also in this bundle" deliverables the plan lists but does not import (LandXML
    /// for the Civil 3D hand-off, contours for linework alongside), the two layers the Forma-parity
    /// steps consume — the vector layers behind road centrelines and site boundaries, and the tree
    /// points behind vegetation — and the imagery drape behind the terrain's texture
    ///.
    /// </para>
    /// <para>
    /// Widened deliberately and narrowly, and still not <c>"all"</c>. <b>The list grows only when a
    /// step that reads a layer lands</b>, because the alternative reading — ask for everything,
    /// decide later — materializes the Unreal mesh into a Revit curator's order at no benefit to
    /// them. The drape is the first entry to arrive by that rule rather than to be held back by it:
    /// it was excluded while the parity row was unimplemented, and it is here now because
    /// <see cref="ImportStepKind.ImageryDrape"/> reads it. The rule cuts both ways or it is not a
    /// rule.
    /// </para>
    /// <para>
    /// It is also the most expensive token on this list by an order of magnitude — a drape is tens of
    /// megabytes where every other Revit deliverable is single-digit — and that cost lands on every
    /// order, including a curator's who never imports terrain. Accepted rather than hidden behind a
    /// conditional list: what the plugin asks the vault for must not depend on document state the
    /// vault cannot see, or two curators on one order would disagree about what the bundle contains.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RevitTokens { get; } =
    [
        "elevation.points_csv",
        "elevation.surface_dxf",
        "buildings.ifc",
        "elevation.landxml",
        "elevation.contours_dxf",
        "vector.geojson",
        "landcover.tree_points_csv",
        "imagery.drape_png",
    ];

    /// <summary>The scope keyword meaning "this host's own set".</summary>
    public const string HostScope = BundleManifestReader.HostKey;

    /// <summary>The scope keyword a curator uses to ask for everything.</summary>
    public const string AllScope = "all";

    /// <summary>
    /// Whether a user-facing scope is one this host offers. Case-insensitive.
    /// </summary>
    /// <remarks>
    /// Only two are valid. A layer token is not a scope: accepting <c>"mesh"</c> here would send a
    /// materialize request the platform reads as a keyword it does not know.
    /// </remarks>
    public static bool IsValidScope(string? scope)
    {
        string trimmed = (scope ?? string.Empty).Trim();
        return trimmed.Equals(HostScope, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(AllScope, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The request body for a scope: the explicit array for this host, the <c>"all"</c> keyword when
    /// the curator explicitly asked for everything.
    /// </summary>
    public static string BuildRequestBody(string scope)
    {
        bool everything = (scope ?? string.Empty).Trim().Equals(AllScope, StringComparison.OrdinalIgnoreCase);

        return everything
            ? """{"tokens":"all"}"""
            : "{\"tokens\":[" + string.Join(",", RevitTokens.Select(token => $"\"{token}\"")) + "]}";
    }

    /// <summary>
    /// Reads a materialize-start response.
    /// </summary>
    /// <remarks>
    /// <c>HPS-24</c>: recognised by BODY SHAPE, not by status code. A response carrying an active
    /// job id is a SUCCESS that joins that job — two curators on one order, or one who clicked
    /// twice, must not queue two ETL runs.
    /// </remarks>
    /// <returns><c>null</c> on success.</returns>
    public static string? TryParseStart(string body, out MaterializeStart start)
    {
        start = default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body ?? string.Empty);
        }
        catch (JsonException)
        {
            return "The materialize response was not valid JSON.";
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "The materialize response was not valid JSON.";
            }

            // Checked BEFORE the error body: a single-flight 409 carries both an active job id and,
            // often, an error-ish message. The job id is the useful half.
            string active = root.Str("activeJobId");
            if (active.Length > 0)
            {
                start = new MaterializeStart(active, AlreadyRunning: true);
                return null;
            }

            if (PlatformErrors.TryRead(root, out PlatformError error))
            {
                return error.Message;
            }

            string jobId = root.Str("jobId");
            if (jobId.Length == 0)
            {
                return "The platform accepted the request but named no job to poll.";
            }

            start = new MaterializeStart(jobId, AlreadyRunning: false);
            return null;
        }
    }

    /// <summary>
    /// Reads one poll response.
    /// </summary>
    /// <remarks>
    /// A <c>failed</c> status is a VALID body: parsing succeeds, the state is
    /// <see cref="MaterializeState.Failed"/>, and the message is surfaced so the curator learns why.
    /// Only a body that states an error INSTEAD of a status fails to parse.
    /// </remarks>
    /// <returns><c>null</c> on success.</returns>
    public static string? TryParseStatus(string body, out MaterializeStatus status)
    {
        status = new MaterializeStatus(MaterializeState.Unknown, MaterializeStatus.Indeterminate, string.Empty, string.Empty);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body ?? string.Empty);
        }
        catch (JsonException)
        {
            return "The materialize status response was not valid JSON.";
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "The materialize status response was not valid JSON.";
            }

            // `state` is an accepted alias, consulted only when `status` is missing.
            string word = root.Str("status");
            if (word.Length == 0)
            {
                word = root.Str("state");
            }

            if (word.Length == 0)
            {
                return PlatformErrors.TryRead(root, out PlatformError error)
                    ? error.Message
                    : "The materialize status response carried no status.";
            }

            MaterializeState state = ParseState(word);

            status = new MaterializeStatus(
                state,
                ReadFraction(root),
                root.Str("jobId"),
                state == MaterializeState.Failed
                    ? PlatformErrors.TryRead(root, out PlatformError failure) ? failure.Message : string.Empty
                    : string.Empty);

            return null;
        }
    }

    /// <summary>
    /// <c>HPS-22</c>: synonym buckets, case-insensitive, and anything unlisted is
    /// <see cref="MaterializeState.Unknown"/> rather than an error.
    /// </summary>
    public static MaterializeState ParseState(string? word)
    {
        string trimmed = (word ?? string.Empty).Trim().ToLowerInvariant();

        return trimmed switch
        {
            "pending" or "queued" or "accepted" or "waiting" => MaterializeState.Pending,
            "processing" or "running" or "in_progress" or "in-progress" or "active" or "materializing" or "started"
                => MaterializeState.Processing,
            "complete" or "completed" or "ready" or "available" or "done" or "succeeded" or "success"
                => MaterializeState.Complete,
            "failed" or "error" or "errored" or "failure" => MaterializeState.Failed,
            _ => MaterializeState.Unknown,
        };
    }

    /// <summary>
    /// Progress as a fraction. A value above 1 is a percentage; divide then clamp. An absent value
    /// is <see cref="MaterializeStatus.Indeterminate"/>, not zero.
    /// </summary>
    private static double ReadFraction(JsonElement root)
    {
        if (root.OptionalDouble("progress") is not { } progress)
        {
            return MaterializeStatus.Indeterminate;
        }

        double fraction = progress > 1.0 ? progress / 100.0 : progress;
        return Math.Clamp(fraction, 0.0, 1.0);
    }
}

/// <summary>A presigned download URL and when it stops working (<c>HPS-29</c>).</summary>
public readonly record struct PresignedDownload(string Url, string ExpiresAt)
{
    /// <summary>
    /// Whether the URL has lapsed, using the same skew as every other expiry in the plugin.
    /// </summary>
    /// <remarks>
    /// An unparseable or absent expiry reads as NOT expired: the URL is minted per import and
    /// seconds old, and refusing to use one because its timestamp was in an unexpected shape turns
    /// a cosmetic platform change into a broken download.
    /// </remarks>
    public bool IsExpired(DateTimeOffset now)
        => DateTimeOffset.TryParse(
            ExpiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset expiry)
            && TokenGrants.IsExpired(now, expiry);
}

/// <summary>Reading a presign response.</summary>
public static class PresignedDownloads
{
    /// <summary>
    /// Parses a presign response. A body with no <c>url</c> is a refusal, and the platform's own
    /// message is what the curator sees — "Not entitled" beats anything this host could invent.
    /// </summary>
    /// <returns><c>null</c> on success.</returns>
    public static string? TryParse(string body, out PresignedDownload download)
    {
        download = default;

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

            download = new PresignedDownload(url, root.Str("expiresAt"));
            return null;
        }
    }
}
