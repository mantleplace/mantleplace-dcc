using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Core;

public enum MaterializeState
{
    Unknown,
    Pending,
    Processing,
    Complete,
    Failed,
}

public enum MaterializeStartOutcome
{
    Started,

    Joined,

    NothingToDo,
}

public readonly record struct MaterializeStart(
    MaterializeStartOutcome Outcome,
    string JobId,
    IReadOnlyList<string> Tokens)
{
    public bool AlreadyRunning => Outcome == MaterializeStartOutcome.Joined;

    public bool WillBuild => Outcome != MaterializeStartOutcome.NothingToDo;

    public static MaterializeStart Started(string jobId, IReadOnlyList<string> tokens)
        => new(MaterializeStartOutcome.Started, jobId, tokens);

    public static MaterializeStart Joined(string jobId, IReadOnlyList<string> tokens)
        => new(MaterializeStartOutcome.Joined, jobId, tokens);

    public static MaterializeStart NothingToDo(IReadOnlyList<string> delivered)
        => new(MaterializeStartOutcome.NothingToDo, string.Empty, delivered);
}

public readonly record struct MissingDeliverable(string Token, string Reason);

public readonly record struct MaterializeStatus(
    MaterializeState State,
    double Fraction,
    string JobId,
    string Message,
    IReadOnlyList<string> Delivered,
    IReadOnlyList<MissingDeliverable> Unproducible)
{
    public const double Indeterminate = -1.0;

    public MaterializeStatus(MaterializeState state, double fraction, string jobId, string message)
        : this(state, fraction, jobId, message, [], [])
    {
    }
}

public static class MaterializeJobs
{
    public const double PollIntervalSeconds = 3.0;

    public const int MaxPolls = 200;

    public const int MaxConsecutivePollFailures = 5;

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

    public const string HostScope = BundleManifestReader.HostKey;

    public const string AllScope = "all";

    public static bool IsValidScope(string? scope)
    {
        string trimmed = (scope ?? string.Empty).Trim();
        return trimmed.Equals(HostScope, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(AllScope, StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildRequestBody(string scope)
    {
        bool everything = (scope ?? string.Empty).Trim().Equals(AllScope, StringComparison.OrdinalIgnoreCase);

        return everything
            ? """{"tokens":"all"}"""
            : "{\"tokens\":[" + string.Join(",", RevitTokens.Select(token => $"\"{token}\"")) + "]}";
    }

    private const string ActiveJobCode = "active_job";

    public static string? TryParseStart(string body, out MaterializeStart start)
    {
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

            if (root.Bool("noop"))
            {
                start = MaterializeStart.NothingToDo(root.StringArray("delivered"));
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

    public static IReadOnlyList<string> RequestedForPolling(MaterializeStart start)
        => start.Tokens is { Count: > 0 } tokens ? tokens : RevitTokens;

    private static readonly string[] PermanentlyAbsentReasons =
        ["no_features_in_aoi", "area_cap_exceeded", "outside_coverage"];

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

    public static string? TryParseStatus(string body, out MaterializeStatus status)
        => TryParseStatus(body, [], out status);

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

    private static string? TerminalFailure(JsonElement root, IReadOnlyCollection<string> outstanding)
    {
        if (root.Object("lastAttempt") is { } attempt
            && attempt.StringArray("tokens").Any(outstanding.Contains))
        {
            return attempt.Str("outcome") switch
            {
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

    private static string DescribeFailure(string reason)
        => ReadinessReasons.ClauseFor(reason) is { } clause
            ? $"The platform could not build this bundle: {clause}."
            : "The platform could not build this bundle.";

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

public readonly record struct PresignedDownload(string Url, string ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now)
        => DateTimeOffset.TryParse(
            ExpiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset expiry)
            && TokenGrants.IsExpired(now, expiry);
}

public static class PresignedDownloads
{
    public const string WholeBundleFormat = "bundle";

    public static string BuildRequestBody() => $$"""{"format":"{{WholeBundleFormat}}"}""";

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
