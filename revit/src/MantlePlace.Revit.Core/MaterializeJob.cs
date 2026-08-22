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

/// <summary>What the platform did with a materialize request (<c>HPS-24</c>).</summary>
/// <remarks>
/// Five wire shapes, four outcomes. Modelling this as "a job id, or an error" was wrong: two of the
/// five shapes are successes that name no job at all, and reading their absence of a job id as a
/// failure is what stopped Revit importing any already-complete bundle.
/// </remarks>
public enum MaterializeStartOutcome
{
    /// <summary>A fresh job. <c>Tokens</c> is the effective set being built.</summary>
    Started,

    /// <summary>
    /// A run was already in flight and this request joined it rather than queueing a second.
    /// <b><c>JobId</c> may be empty</b> — see the remarks on <see cref="MaterializeJobs.TryParseStart"/>.
    /// </summary>
    Joined,

    /// <summary>
    /// Nothing to build: everything asked for is already delivered, or is permanently unavailable
    /// for this area. <c>Tokens</c> is what the bundle ALREADY has. There is no job, so there is
    /// nothing to poll — the caller goes straight to the download.
    /// </summary>
    NothingToDo,

    /// <summary>
    /// The order's core build has not finished, so the picks are parked and fire automatically when
    /// it does. <c>Tokens</c> is the parked set. No job exists yet.
    /// </summary>
    Queued,
}

/// <summary>The response to starting a materialize.</summary>
/// <param name="Outcome">Which of the four things happened.</param>
/// <param name="JobId">The job to poll. Empty for every outcome except a started or joined run.</param>
/// <param name="Tokens">
/// The token list this outcome names — the effective set being built, the delivered set, or the
/// parked set, according to <paramref name="Outcome"/>.
/// </param>
public readonly record struct MaterializeStart(
    MaterializeStartOutcome Outcome,
    string JobId,
    IReadOnlyList<string> Tokens)
{
    /// <summary>Whether this joined a run rather than starting one (<c>HPS-24</c>).</summary>
    public bool AlreadyRunning => Outcome == MaterializeStartOutcome.Joined;

    /// <summary>Whether anything will be produced. False only when there was nothing to do.</summary>
    public bool WillBuild => Outcome != MaterializeStartOutcome.NothingToDo;

    public static MaterializeStart Started(string jobId, IReadOnlyList<string> tokens)
        => new(MaterializeStartOutcome.Started, jobId, tokens);

    public static MaterializeStart Joined(string jobId, IReadOnlyList<string> tokens)
        => new(MaterializeStartOutcome.Joined, jobId, tokens);

    public static MaterializeStart NothingToDo(IReadOnlyList<string> delivered)
        => new(MaterializeStartOutcome.NothingToDo, string.Empty, delivered);

    public static MaterializeStart Queued(IReadOnlyList<string> pending)
        => new(MaterializeStartOutcome.Queued, string.Empty, pending);
}

/// <summary>A requested deliverable this bundle will never carry, and the platform's reason.</summary>
public readonly record struct MissingDeliverable(string Token, string Reason);

/// <summary>One poll of a materialize job.</summary>
/// <param name="State">Where it stands.</param>
/// <param name="Fraction">
/// Progress in <c>[0, 1]</c>, or <b>-1 for indeterminate</b>. A progress bar sitting at 0% and a
/// spinner say different things to a curator deciding whether to wait.
/// </param>
/// <param name="JobId">Echoed when the body carries it.</param>
/// <param name="Message">The platform's reason, when a job failed.</param>
/// <param name="Delivered">
/// Which of the REQUESTED tokens the bundle now carries. Empty on the legacy job-status shape,
/// which does not report delivery.
/// </param>
/// <param name="Unproducible">
/// Requested tokens the platform will never produce for this area, with its reason. A gap, not a
/// failure: waiting for one is waiting forever, so these are reported and stepped over.
/// </param>
public readonly record struct MaterializeStatus(
    MaterializeState State,
    double Fraction,
    string JobId,
    string Message,
    IReadOnlyList<string> Delivered,
    IReadOnlyList<MissingDeliverable> Unproducible)
{
    /// <summary>What an absent progress value means. Not zero.</summary>
    public const double Indeterminate = -1.0;

    /// <summary>The job-status shape, which reports no delivery facts.</summary>
    public MaterializeStatus(MaterializeState state, double fraction, string jobId, string message)
        : this(state, fraction, jobId, message, [], [])
    {
    }
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

    /// <summary>The <c>code</c> the platform sends when a run is already in flight.</summary>
    private const string ActiveJobCode = "active_job";

    /// <summary>
    /// Reads a materialize-start response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔<c>HPS-24</c>: recognised by BODY SHAPE, not by status code, and <b>each outcome is keyed
    /// on its own marker — never on the absence of <c>jobId</c></b>. That inference is what broke
    /// this: two of the platform's five shapes are successes carrying no job at all, and both read
    /// as "the platform accepted the request but named no job to poll".
    /// </para>
    /// <para>
    /// <c>noop</c> and <c>queued</c> are read FIRST because they are unambiguous discriminators; a
    /// body carrying either cannot be anything else. The join test comes before the error body
    /// because the single-flight response carries both a job fact and error-ish prose, and the job
    /// fact is the useful half.
    /// </para>
    /// <para>
    /// ⚠️ <b>A join with no job id is still a join.</b> The 409's <c>activeJobId</c> may be
    /// <c>null</c>, and polling is keyed on the ORDER, not the job — <c>PollOnceAsync</c> GETs the
    /// order's materialize URL and never took a job id — so an unnamed in-flight run is fully
    /// followable. Reporting the platform's prose as an error instead would tell a curator to retry
    /// a run that is already going, which is the exact failure this rule exists to prevent.
    /// </para>
    /// </remarks>
    /// <returns><c>null</c> on success.</returns>
    public static string? TryParseStart(string body, out MaterializeStart start)
    {
        // A well-formed empty value rather than `default`, so a caller that reads `Tokens` off a
        // failed parse gets an empty list instead of a null reference.
        start = MaterializeStart.NothingToDo([]);

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

            // Nothing to build: everything asked for is already delivered, or is permanently
            // unavailable here. A success, and the one the caller acts on most.
            if (root.Bool("noop"))
            {
                start = MaterializeStart.NothingToDo(root.StringArray("delivered"));
                return null;
            }

            // The order's core build has not finished; the picks are parked and fire on their own.
            if (root.Bool("queued"))
            {
                start = MaterializeStart.Queued(root.StringArray("pendingTokens"));
                return null;
            }

            string active = root.Str("activeJobId");
            bool joined = active.Length > 0
                || root.Bool("coalesced")
                || string.Equals(root.Str("code"), ActiveJobCode, StringComparison.Ordinal);

            if (joined)
            {
                start = MaterializeStart.Joined(active, root.StringArray("tokens"));
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

            start = MaterializeStart.Started(jobId, root.StringArray("tokens"));
            return null;
        }
    }

    /// <summary>
    /// The token set to measure delivery against while polling.
    /// </summary>
    /// <remarks>
    /// The start response echoes the EFFECTIVE set — already deduped against what is delivered and
    /// against what can never be produced here — so it is both more precise than this host's own
    /// list and the only correct answer for the <c>"all"</c> scope, whose expansion lives on the
    /// server. Falls back to <see cref="RevitTokens"/> when the body named none.
    /// </remarks>
    public static IReadOnlyList<string> RequestedForPolling(MaterializeStart start)
        => start.Tokens is { Count: > 0 } tokens ? tokens : RevitTokens;

    /// <summary>
    /// Reasons a deliverable is absent that no amount of waiting or retrying will change.
    /// </summary>
    /// <remarks>
    /// Mirrors the platform's own non-retryable set exactly. Treating one of these as outstanding
    /// makes a bundle that is as complete as it will ever be poll for its whole budget and then time
    /// out, having been finished the entire time.
    /// </remarks>
    private static readonly string[] PermanentlyAbsentReasons =
        ["no_features_in_aoi", "area_cap_exceeded", "outside_coverage"];

    /// <summary>
    /// Reasons that mean nobody has tried yet, rather than that an attempt produced nothing.
    /// </summary>
    private static readonly string[] NotAttemptedReasons = ["available_on_request", "not_selected"];

    /// <summary>Reads one poll response.</summary>
    /// <remarks>
    /// <para>
    /// <b>Two shapes.</b> A body carrying a <c>status</c> (or its <c>state</c> alias) is a job-status
    /// document and is read as one. Otherwise the platform answers this endpoint with a
    /// DELIVERY-STATE document — <c>delivered</c>, <c>notDelivered</c>, <c>activeJob</c> — which
    /// carries no status word at all, and completion has to be derived from it.
    /// </para>
    /// <para>
    /// That derivation is the more truthful reading either way: a materialize run reports
    /// <c>completed</c> even when every token's emit failed, because per-token errors are swallowed
    /// by a soft-fail envelope. <b>Delivery is proof of production; a job status is not.</b>
    /// </para>
    /// <para>
    /// A <c>failed</c> status is a VALID body: parsing succeeds, the state is
    /// <see cref="MaterializeState.Failed"/>, and the message is surfaced so the curator learns why.
    /// Only a body that states an error INSTEAD of either shape fails to parse.
    /// </para>
    /// </remarks>
    /// <param name="requested">
    /// The tokens whose delivery decides completion. Empty means only the job-status shape yields a
    /// state — see the single-argument overload.
    /// </param>
    /// <returns><c>null</c> on success.</returns>
    public static string? TryParseStatus(
        string body,
        IReadOnlyCollection<string> requested,
        out MaterializeStatus status)
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

            if (word.Length > 0)
            {
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

            // `delivered` is the discriminator: the delivery-state document always declares it,
            // where `activeJob` is legitimately null on an idle order and every other field is
            // optional. Keying on an optional field would misread an idle bundle as a foreign shape.
            if (root.Array("delivered") is not null)
            {
                status = DeriveDelivery(root, requested);
                return null;
            }

            return PlatformErrors.TryRead(root, out PlatformError error)
                ? error.Message
                : "The materialize status response carried no status.";
        }
    }

    /// <summary>
    /// As <see cref="TryParseStatus(string, IReadOnlyCollection{string}, out MaterializeStatus)"/>
    /// with no requested set, so only the job-status shape yields a state.
    /// </summary>
    public static string? TryParseStatus(string body, out MaterializeStatus status)
        => TryParseStatus(body, [], out status);

    /// <summary>
    /// Derives a state from the platform's delivery-state document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row order below IS the design, and three rows are traps.
    /// </para>
    /// <para>
    /// ⛔ <b>Unreadable delivery state is checked first.</b> <c>deliveryStateUnknown</c> means the
    /// platform could not read what the bundle has, so <c>delivered</c> is empty for want of an
    /// answer rather than because the bundle is empty. Falling through with an empty requested set
    /// would compute "nothing outstanding" and report Complete — handing a curator a bundle the
    /// platform never confirmed. <see cref="MaterializeState.Unknown"/> is not terminal, so polling
    /// continues and the poll budget provides the honest ending.
    /// </para>
    /// <para>
    /// ⛔ <b>Nothing outstanding beats a running job.</b> If everything asked for is on hand, an
    /// in-flight job is building someone else's pick, and waiting on it stalls a download that is
    /// already possible.
    /// </para>
    /// <para>
    /// ⛔ <b>A terminal attempt that left tokens outstanding is a failure whatever it called
    /// itself.</b> The verdict keys on the tokens, not on <c>outcome</c>; <c>outcome</c> only picks
    /// the sentence. Reading <c>completed</c> as success is the silent loop where a curator
    /// regenerates forever; reading it as "still working" is the ten-minute hang.
    /// </para>
    /// <para>
    /// The last row is Pending, never Complete: nothing outstanding, nothing running and nothing
    /// failed means the job row is not visible yet, which is the normal state in the seconds after a
    /// start. <c>activeJob.steps</c> is deliberately NOT read for progress — it is an unvalidated
    /// jsonb ladder the platform owns, and a newer worker's shape must not become this host's
    /// progress source.
    /// </para>
    /// </remarks>
    private static MaterializeStatus DeriveDelivery(JsonElement root, IReadOnlyCollection<string> requested)
    {
        HashSet<string> delivered = new(root.StringArray("delivered"), StringComparer.Ordinal);

        List<MissingDeliverable> unproducible = [];
        if (root.Array("notDelivered") is { } notDelivered)
        {
            foreach (JsonElement row in notDelivered.EnumerateArray())
            {
                string token = row.Str("token");
                string reason = row.Str("reason");
                if (token.Length > 0
                    && requested.Contains(token)
                    && System.Array.IndexOf(PermanentlyAbsentReasons, reason) >= 0)
                {
                    unproducible.Add(new MissingDeliverable(token, reason));
                }
            }
        }

        IReadOnlyList<string> mine = [.. requested.Where(delivered.Contains)];
        HashSet<string> blocked = new(unproducible.Select(row => row.Token), StringComparer.Ordinal);
        List<string> outstanding =
            [.. requested.Where(token => !delivered.Contains(token) && !blocked.Contains(token))];

        double fraction = requested.Count == 0
            ? MaterializeStatus.Indeterminate
            : mine.Count / (double)requested.Count;

        // ⛔ No yardstick, no verdict. "Nothing is outstanding" is vacuously true against an empty
        // requested set, so falling through would report Complete for a bundle nobody asked anything
        // of. There is no honest answer here, and Unknown is not terminal, so the caller keeps
        // polling rather than downloading on the strength of a tautology.
        if (requested.Count == 0)
        {
            return new MaterializeStatus(
                MaterializeState.Unknown,
                MaterializeStatus.Indeterminate,
                string.Empty,
                "No deliverables were named, so there is nothing to check against.",
                mine,
                unproducible);
        }

        if (root.Bool("deliveryStateUnknown"))
        {
            return new MaterializeStatus(
                MaterializeState.Unknown,
                MaterializeStatus.Indeterminate,
                string.Empty,
                "The platform could not confirm what this bundle already has. Still checking…",
                mine,
                unproducible);
        }

        if (outstanding.Count == 0)
        {
            return new MaterializeStatus(
                MaterializeState.Complete,
                1.0,
                string.Empty,
                string.Empty,
                mine,
                unproducible);
        }

        if (root.Object("activeJob") is { } activeJob)
        {
            int building = activeJob.StringArray("tokens").Count;
            return new MaterializeStatus(
                MaterializeState.Processing,
                fraction,
                activeJob.Str("id"),
                building > 0 ? $"Building {building} deliverable(s)…" : "Building…",
                mine,
                unproducible);
        }

        if (TerminalFailure(root, outstanding) is { } terminal)
        {
            return new MaterializeStatus(
                MaterializeState.Failed,
                MaterializeStatus.Indeterminate,
                string.Empty,
                terminal,
                mine,
                unproducible);
        }

        return new MaterializeStatus(
            MaterializeState.Pending,
            fraction,
            string.Empty,
            "Waiting for the platform to pick this up…",
            mine,
            unproducible);
    }

    /// <summary>
    /// The sentence for a terminal attempt that left work undone, or <c>null</c> when no terminal
    /// attempt touched what is still outstanding.
    /// </summary>
    private static string? TerminalFailure(JsonElement root, IReadOnlyCollection<string> outstanding)
    {
        if (root.Object("lastAttempt") is { } attempt
            && attempt.StringArray("tokens").Any(outstanding.Contains))
        {
            return attempt.Str("outcome") switch
            {
                // ⛔ The soft-fail envelope: the run says it finished, and produced none of it.
                "completed" =>
                    "The platform reported the job finished but produced none of these deliverables. "
                    + "Try preparing again; if it repeats, the platform could not build them.",
                "cancelled" => "The platform's generation timed out and was swept. Try preparing again.",
                _ => DescribeFailure(attempt.Str("reason")),
            };
        }

        if (root.Object("lastFailed") is { } failed
            && failed.StringArray("tokens").Any(outstanding.Contains))
        {
            return DescribeFailure(failed.Str("reason"));
        }

        return null;
    }

    /// <summary>The platform's own reason for a failed run, in a sentence, with a plain fallback.</summary>
    private static string DescribeFailure(string reason)
        => ReadinessReasons.ClauseFor(reason) is { } clause
            ? $"The platform could not build this bundle: {clause}."
            : "The platform could not build this bundle.";

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
