using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Host-local coverage for the materialize contract: every response shape the platform can send,
/// and every row of the delivery-state decision table.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the corpus drove only the two shapes that carry a job id. The platform sends
/// five, and the three that were unasserted are the ones that broke: a bundle with nothing left to
/// build reported <i>"the platform accepted the request but named no job to poll"</i> and Revit
/// could not import anything, because the download only happens on the far side of that parse.
/// </para>
/// <para>
/// The lesson worth keeping is narrower than "add cases". Both hosts inferred an outcome from the
/// ABSENCE of a field — no <c>jobId</c> means failure — and absence is the one thing a growing
/// protocol reassigns for free. Every case below keys on a marker that is present.
/// </para>
/// </remarks>
internal static class MaterializeJobTests
{
    /// <summary>The eight tokens the Revit scope asks for, as the poll's yardstick.</summary>
    private static IReadOnlyCollection<string> Requested => MaterializeJobs.RevitTokens;

    internal static int Run()
    {
        TestRun run = new();

        // ------------------------------------------------------------------ start shapes

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
            // The coalesce body names no `jobId` at all. A host reading only `jobId` sees "no id"
            // here and reports a failure for a request that succeeded and joined a running build.
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
            // The platform may report an in-flight run without naming it. This is still followable:
            // polling is keyed on the ORDER, not the job — the status GET never took a job id. A
            // host that refuses here tells a curator to retry a build that is already running, which
            // is the exact failure HPS-24 exists to prevent. Do not "fix" this back into a refusal.
            string? error = MaterializeJobs.TryParseStart(
                """{"error":"A materialize job is already in flight for this order","code":"active_job","activeJobId":null}""",
                out MaterializeStart start);

            run.Equal(error, null, "an unnamed in-flight job is still a join");
            run.True(start.Outcome == MaterializeStartOutcome.Joined, "outcome is Joined");
            run.Equal(start.JobId, "", "no job id was named, and none is needed");
        });

        run.Case("200 noop: nothing to build is a SUCCESS carrying no job", () =>
        {
            // The reported bug, in one line. This body used to produce "the platform accepted the
            // request but named no job to poll" and dead-ended the only path that downloads.
            string? error = MaterializeJobs.TryParseStart(
                """{"noop":true,"delivered":["elevation.points_csv","buildings.ifc"]}""",
                out MaterializeStart start);

            run.Equal(error, null, "nothing to build is not an error");
            run.True(start.Outcome == MaterializeStartOutcome.NothingToDo, "outcome is NothingToDo");
            run.Equal(start.JobId, "", "there is no job");
            run.Equal(start.Tokens.Count, 2, "delivered tokens are carried");
            run.False(start.WillBuild, "nothing will be built");
        });

        run.Case("202 queued: the picks are parked until the core build finishes", () =>
        {
            string? error = MaterializeJobs.TryParseStart(
                """{"queued":true,"pendingTokens":["buildings.ifc","elevation.landxml"]}""",
                out MaterializeStart start);

            run.Equal(error, null, "a queued pick is not an error");
            run.True(start.Outcome == MaterializeStartOutcome.Queued, "outcome is Queued");
            run.Equal(start.Tokens.Count, 2, "pending tokens are carried");
        });

        run.Case("an error body still refuses, in the platform's own words", () =>
        {
            string? error = MaterializeJobs.TryParseStart("""{"error":"Internal error"}""", out _);
            run.Equal(error, "Internal error", "the platform's message survives");
        });

        run.Case("an unrecognised object still names no job to poll", () =>
        {
            // The original message keeps its job — but now it means only what it says: a body that
            // is none of the five known shapes.
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

        // ------------------------------------------------------------------ status: legacy shape

        run.Case("a job-status body still parses exactly as before", () =>
        {
            // The platform does not send this on the vault endpoint, but the corpus pins it and a
            // deployment that does must keep working. This is the guard on that branch.
            string? error = MaterializeJobs.TryParseStatus(
                """{"status":"running","progress":42,"jobId":"job-9"}""",
                Requested,
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Processing, "running maps to Processing");
            run.Within(status.Fraction, 0.42, 0.0001, "42 is a percentage");
            run.Equal(status.JobId, "job-9", "job id");
        });

        // ------------------------------------------------------------------ status: delivery shape

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
            // deliveryStateUnknown means `delivered` is empty for want of an answer, not because the
            // bundle is empty. With an EMPTY requested set the "nothing outstanding" test would
            // otherwise pass vacuously and hand over a bundle the platform never confirmed. Unknown
            // is not terminal, so the poll budget provides the honest ending.
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
            // The worker's soft-fail envelope swallows per-token emit errors, so a run that produced
            // none of what it was asked for still reports `completed`. Believing it is the silent
            // loop where a curator regenerates forever; calling it "still working" is the
            // ten-minute hang. The verdict keys on the TOKENS, not on the word.
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
            // Waiting for a deliverable the platform will never produce for this area is waiting
            // forever: the bundle is as complete as it will ever be. Treating these as outstanding
            // is what makes a finished bundle poll its whole budget and then time out.
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
            // The normal state in the seconds after a start: the job row is not visible yet.
            // Reporting Complete here sends the caller on to download a bundle without the layers it
            // just asked for.
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
            // "Nothing is outstanding" is trivially true of an empty requested set. Reporting
            // Complete off that tautology would hand over a bundle on the strength of having asked
            // it for nothing. Caught by this suite before it could ship.
            string? error = MaterializeJobs.TryParseStatus(
                Dto(delivered: [], activeJob: """{"id":"j","tokens":[]}"""),
                [],
                out MaterializeStatus status);

            run.Equal(error, null, "no parse error");
            run.True(status.State == MaterializeState.Unknown, "state is Unknown, not Complete");
            run.Within(status.Fraction, MaterializeStatus.Indeterminate, 0.0001, "indeterminate, not 0");
        });

        // ---- the presign request ------------------------------------------------------------
        // The download only happens if the ask is well-formed. The route validates its body against
        // a schema, so the empty object this host used to send came back 400 "Invalid request" —
        // and, until the detail read below, with nothing naming the missing field.
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

        // ---- a refusal explains itself ------------------------------------------------------
        // `Message` stays the HPS-48 precedence read; `Sentence` adds what the platform said about
        // why. A schema rejection puts the only actionable half in a sibling.
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

    /// <summary>A delivery-state document with only the fields a case cares about.</summary>
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
