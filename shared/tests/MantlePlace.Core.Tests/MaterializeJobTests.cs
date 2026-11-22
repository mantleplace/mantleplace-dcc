using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class MaterializeJobTests
{
    private static IReadOnlyCollection<string> Requested => MaterializeJobs.RevitTokens;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("201 created: a fresh job, with the effective token set", () =>
        {
            string? error = MaterializeJobs.TryParseStart(
                """{"jobId":"job-123","tokens":["buildings.ifc","elevation.points_csv"]}""",
                out MaterializeStart start);

            run.Equal(error, null, "no parse error");
            run.True(start.Outcome == MaterializeStartOutcome.Started, "outcome is Started");
            run.Equal(start.JobId, "job-123", "job id");
            run.Equal(start.Tokens.Count, 2, "effective token count");
            run.True(start.WillBuild, "a started job will build");
        });

        run.Case("200 coalesced: joined on activeJobId, which is NOT jobId", () =>
        {
            string? error = MaterializeJobs.TryParseStart(
                """{"coalesced":true,"activeJobId":"job-existing","tokens":["mesh.glb"]}""",
                out MaterializeStart start);

            run.Equal(error, null, "no parse error");
            run.True(start.Outcome == MaterializeStartOutcome.Joined, "outcome is Joined");
            run.Equal(start.JobId, "job-existing", "job id comes from activeJobId");
            run.True(start.AlreadyRunning, "AlreadyRunning still reads true (HPS-24)");
        });

        run.Case("409 single-flight: the job fact beats the error prose", () =>
        {
            string? error = MaterializeJobs.TryParseStart(
                """{"error":"A materialize job is already in flight for this order","code":"active_job","activeJobId":"job-live"}""",
                out MaterializeStart start);

            run.Equal(error, null, "an in-flight job is a success, not an error");
            run.True(start.Outcome == MaterializeStartOutcome.Joined, "outcome is Joined");
            run.Equal(start.JobId, "job-live", "job id");
        });

        run.Case("⛔409 with a NULL active job id is still a join, never an error", () =>
        {
            string? error = MaterializeJobs.TryParseStart(
                """{"error":"A materialize job is already in flight for this order","code":"active_job","activeJobId":null}""",
                out MaterializeStart start);

            run.Equal(error, null, "an unnamed in-flight job is still a join");
            run.True(start.Outcome == MaterializeStartOutcome.Joined, "outcome is Joined");
            run.Equal(start.JobId, "", "no job id was named, and none is needed");
        });

        run.Case("200 noop: nothing to build is a SUCCESS carrying no job", () =>
        {
            string? error = MaterializeJobs.TryParseStart(
                """{"noop":true,"delivered":["elevation.points_csv","buildings.ifc"]}""",
                out MaterializeStart start);

            run.Equal(error, null, "nothing to build is not an error");
            run.True(start.Outcome == MaterializeStartOutcome.NothingToDo, "outcome is NothingToDo");
            run.Equal(start.JobId, "", "there is no job");
            run.Equal(start.Tokens.Count, 2, "delivered tokens are carried");
            run.False(start.WillBuild, "nothing will be built");
        });

        run.Case("an error body still refuses, in the platform's own words", () =>
        {
            string? error = MaterializeJobs.TryParseStart("""{"error":"Internal error"}""", out _);
            run.Equal(error, "Internal error", "the platform's message survives");
        });

        run.Case("an unrecognised object still names no job to poll", () =>
        {
            string? error = MaterializeJobs.TryParseStart("""{"ok":true}""", out _);
            run.Contains(error, "named no job to poll", "the unrecognised-shape refusal");
        });

        run.Case("a numeric jobId reads as absent rather than as a job", () =>
        {
            string? error = MaterializeJobs.TryParseStart("""{"jobId":12345}""", out _);
            run.Contains(error, "named no job to poll", "a non-string id is not an id");
        });

        run.Case("polling measures against the server's effective set, falling back to ours", () =>
        {
            MaterializeStart withTokens = MaterializeStart.Started("j", ["mesh.glb"]);
            run.Equal(MaterializeJobs.RequestedForPolling(withTokens).Count, 1, "the server's set wins");

            MaterializeStart without = MaterializeStart.Joined("j", []);
            run.Equal(
                MaterializeJobs.RequestedForPolling(without).Count,
                MaterializeJobs.RevitTokens.Count,
                "falls back to this host's own list");
        });

        run.Case("a job-status body still parses exactly as before", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                """{"status":"running","progress":42,"jobId":"job-9"}""",
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Processing, "running maps to Processing");
            run.Within(status.Fraction, 0.42, 0.0001, "42 is a percentage");
            run.Equal(status.JobId, "job-9", "job id");
        });

        run.Case("everything requested is delivered: Complete", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: Requested, activeJob: null),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Complete, "state is Complete");
            run.Within(status.Fraction, 1.0, 0.0001, "fraction is 1");
        });

        run.Case("a job in flight is Processing, with the id off activeJob", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: ["imagery.drape_png"],
                    activeJob: """{"id":"job-7","tokens":["buildings.ifc","elevation.landxml"]}"""),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Processing, "state is Processing");
            run.Equal(status.JobId, "job-7", "job id comes from activeJob.id");
            run.Contains(status.Message, "Building 2", "names how many are building");
            run.Within(status.Fraction, 1.0 / 8.0, 0.0001, "fraction is delivered over requested");
        });

        run.Case("⛔unreadable delivery state is Unknown, and NEVER Complete", () =>
        {
            string body = """{"deliveryStateUnknown":true,"delivered":[],"base":[],"notDelivered":[],"activeJob":null}""";

            string? error = MaterializeJobs.TryParseStatus(body, Requested, out MaterializeStatus status);
            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Unknown, "state is Unknown with tokens requested");

            error = MaterializeJobs.TryParseStatus(body, [], out MaterializeStatus empty);
            run.Equal(error, null, "no parse error");
            run.True(empty.State == MaterializeState.Unknown, "state is Unknown with NOTHING requested");
        });

        run.Case("⛔a run that says 'completed' but delivered nothing is a FAILURE", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: [],
                    activeJob: null,
                    lastAttempt: """{"tokens":["buildings.ifc"],"outcome":"completed","reason":null}"""),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Failed, "state is Failed");
            run.Contains(status.Message, "produced none", "says the run produced nothing");
        });

        run.Case("a swept run reports as a timeout, not as silence", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: [],
                    activeJob: null,
                    lastAttempt: """{"tokens":["buildings.ifc"],"outcome":"cancelled","reason":null}"""),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Failed, "state is Failed");
            run.Contains(status.Message, "timed out", "names the sweep");
        });

        run.Case("a terminal run for OTHER tokens does not fail this one", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: [],
                    activeJob: null,
                    lastAttempt: """{"tokens":["mesh.usdz"],"outcome":"failed","reason":"emit_failed"}"""),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Pending, "someone else's failure is not ours");
        });

        run.Case("⛔partial delivery whose remainder can never be built is Complete", () =>
        {
            List<string> delivered = [.. Requested];
            delivered.Remove("buildings.ifc");

            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: delivered,
                    activeJob: null,
                    notDelivered: """[{"token":"buildings.ifc","reason":"no_features_in_aoi"}]"""),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Complete, "state is Complete");
            run.Equal(status.Unproducible.Count, 1, "the gap is reported, not hidden");
            run.Equal(status.Unproducible[0].Token, "buildings.ifc", "which deliverable");
            run.Equal(status.Unproducible[0].Reason, "no_features_in_aoi", "and the platform's reason");
        });

        run.Case("an outstanding token that failed to emit IS retryable, and reports as failed", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: [],
                    activeJob: null,
                    notDelivered: """[{"token":"buildings.ifc","reason":"emit_failed"}]""",
                    lastAttempt: """{"tokens":["buildings.ifc"],"outcome":"failed","reason":"emit_failed"}"""),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Failed, "state is Failed");
            run.Equal(status.Unproducible.Count, 0, "emit_failed is retryable, so not a permanent gap");
        });

        run.Case("'nobody tried yet' is Pending, not a failure", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: [],
                    activeJob: null,
                    notDelivered: """[{"token":"buildings.ifc","reason":"available_on_request"}]"""),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Pending, "state is Pending");
        });

        run.Case("⛔nothing delivered, nothing running, nothing failed is Pending — not Complete", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: [], activeJob: null),
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Pending, "state is Pending");
        });

        run.Case("an error body with no delivery facts still refuses", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                """{"error":"Order not found"}""", Requested, out _);

            run.Equal(error, "Order not found", "the platform's message survives");
        });

        run.Case("a body that is neither shape names the missing status", () =>
        {
            string? error = MaterializeJobs.TryParseStatus("""{"ok":true}""", Requested, out _);
            run.Contains(error, "carried no status", "the unrecognised-shape refusal");
        });

        run.Case("⛔with nothing requested there is no verdict — never a vacuous Complete", () =>
        {
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: [], activeJob: """{"id":"j","tokens":[]}"""),
                [],
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Unknown, "state is Unknown, not Complete");
            run.Within(status.Fraction, MaterializeStatus.Indeterminate, 0.0001, "indeterminate, not 0");
        });

        run.Case("presign body names the whole archive", () =>
        {
            run.Equal(
                PresignedDownloads.BuildRequestBody(),
                """{"format":"bundle"}""",
                "the presign body names the whole archive");

            run.False(
                PresignedDownloads.BuildRequestBody().Contains("glb", StringComparison.OrdinalIgnoreCase),
                "and never the deprecated alias, which returns a MESH when the order carries one");
        });

        run.Case("a schema rejection names the field it rejected", () => run.Equal(
            PlatformErrors.FromBody(
                """{"error":"Invalid request","issues":[{"path":["format"],"message":"Required"}]}""")!
                .Value.Sentence,
            "Invalid request — format: Required",
            "issues render as field: reason"));

        run.Case("detail prose is carried the same way", () => run.Equal(
            PlatformErrors.FromBody("""{"error":"Invalid request","code":"invalid_body","detail":"tokens: expected array"}""")!
                .Value.Sentence,
            "Invalid request — tokens: expected array",
            "detail"));

        run.Case("a body with no detail reads as it always did", () => run.Equal(
            PlatformErrors.FromBody("""{"error":"Not entitled"}""")!.Value.Sentence,
            "Not entitled",
            "unchanged"));

        run.Case("an issue with no message contributes no empty clause", () => run.Equal(
            PlatformErrors.FromBody("""{"error":"Invalid request","issues":[{"path":["format"]}]}""")!
                .Value.Sentence,
            "Invalid request",
            "skipped"));

        run.Case("a non-array issues field never throws", () => run.Equal(
            PlatformErrors.FromBody("""{"error":"Invalid request","issues":"nope"}""")!.Value.Message,
            "Invalid request",
            "total accessors"));

        return run.Report("materialize contract");
    }

    private static string Dto(
        IEnumerable<string> delivered,
        string? activeJob,
        string notDelivered = "[]",
        string? lastAttempt = null)
    {
        string deliveredJson = "[" + string.Join(",", delivered.Select(token => $"\"{token}\"")) + "]";

        return "{"
            + "\"deliveryModel\":\"base_on_demand\","
            + $"\"delivered\":{deliveredJson},"
            + "\"base\":[],"
            + $"\"notDelivered\":{notDelivered},"
            + $"\"activeJob\":{activeJob ?? "null"},"
            + "\"lastFailed\":null,"
            + $"\"lastAttempt\":{lastAttempt ?? "null"},"
            + "\"pendingRetryTokens\":[]"
            + "}";
    }
}
