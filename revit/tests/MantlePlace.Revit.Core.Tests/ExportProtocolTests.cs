using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The export protocol, decided without a platform or a Revit: the presence payload, the export in
/// both its spellings, the lifecycle a plugin may send, the pace of progress reports, and when a
/// bundle is ready (the platform export contract, protocol 1).
/// </summary>
internal static class ExportProtocolTests
{
    private const string Connection = "revit-0123456789abcdef";

    // The contract's table of what a plugin may send, written out here from the document and not
    // derived from the code under test. A move absent from this list must be refused.
    private static readonly (ExportStatus From, string Move)[] Allowed =
    [
        (ExportStatus.Sent, "received"),
        (ExportStatus.Received, "waiting_for_bundle"),
        (ExportStatus.Received, "awaiting_options"),
        (ExportStatus.WaitingForBundle, "awaiting_options"),
        (ExportStatus.AwaitingOptions, "start"),
        (ExportStatus.AwaitingOptions, "progress"), // a progress sent with the first call is ignored
        (ExportStatus.AwaitingOptions, "cancelled"),
        (ExportStatus.Importing, "progress"),
        (ExportStatus.Importing, "imported"),
        (ExportStatus.Sent, "failed"),
        (ExportStatus.Received, "failed"),
        (ExportStatus.WaitingForBundle, "failed"),
        (ExportStatus.AwaitingOptions, "failed"),
        (ExportStatus.Importing, "failed"),
    ];

    internal static int Run()
    {
        TestRun run = new();
        PresenceCases(run);
        ConnectionIdCases(run);
        ParsingCases(run);
        LifecycleCases(run);
        PacingCases(run);
        ReadinessCases(run);
        UnreadableCases(run);
        return run.Report("export protocol");
    }

    private static void PresenceCases(TestRun run)
    {
        run.Case("the presence payload names protocol 1, the revit host, and the open document", () =>
        {
            ConnectedHost host = ConnectedHosts.Announce("2026.2", "0.2.0", "Site.rvt", "DESK-7");

            run.Equal(host.Protocol, 1, "protocol");
            run.Equal(host.Host, "revit", "host");
            run.Equal(host.HostVersion, "2026.2", "host version");
            run.Equal(host.PluginVersion, "0.2.0", "plugin version");
            run.Equal(host.Document, "Site.rvt", "document");
            run.Equal(host.Machine, "DESK-7", "machine");
        });

        run.Case("with no document open the document is none, not an empty string", () =>
        {
            run.True(ConnectedHosts.Announce("2026", "0.2.0", null, "m").Document is null, "null stays null");
            run.True(ConnectedHosts.Announce("2026", "0.2.0", "  ", "m").Document is null, "blank is none");
        });

        run.Case("the manifest range is the reader's own floor and newest major", () =>
        {
            ConnectedHost host = ConnectedHosts.Announce("2026", "0.2.0", null, "m");

            run.Equal(host.ReadsMin, ManifestVersions.MinSupportedManifestVersion, "reads.min is the one floor");
            run.Equal(host.ReadsMaxMajor, ManifestVersions.NewestSupportedMajor, "reads.maxMajor is the newest major");
            run.True(ManifestVersion.Parse(host.ReadsMin).IsValid, "and the floor is a MAJOR.MINOR.PATCH version");
        });

        run.Case("the range matches what the reader refuses, at both ends", () =>
        {
            ConnectedHost host = ConnectedHosts.Announce("2026", "0.2.0", null, "m");

            run.True(ExportBundles.CanRead(host.ReadsMin), "a bundle at reads.min is read");
            run.True(ExportBundles.CanRead($"{host.ReadsMaxMajor}.99.99"), "and any minor of reads.maxMajor");
            run.False(ExportBundles.CanRead($"{host.ReadsMaxMajor + 1}.0.0"), "a major above it is not");
        });

        run.Case("the payload serialises as the channel carries it", () =>
        {
            using JsonDocument document = JsonDocument.Parse(
                ConnectedHosts.Announce("2026.2", "0.2.0", "Site.rvt", "DESK-7").ToJson());
            JsonElement root = document.RootElement;

            run.Equal(root.GetProperty("protocol").GetInt32(), 1, "protocol is a number");
            run.Equal(root.GetProperty("host").GetString(), "revit", "host");
            run.Equal(root.GetProperty("hostVersion").GetString(), "2026.2", "hostVersion");
            run.Equal(root.GetProperty("pluginVersion").GetString(), "0.2.0", "pluginVersion");
            run.Equal(root.GetProperty("document").GetString(), "Site.rvt", "document");
            run.Equal(root.GetProperty("machine").GetString(), "DESK-7", "machine");
            JsonElement reads = root.GetProperty("reads");
            run.Equal(reads.GetProperty("min").GetString(), ManifestVersions.MinSupportedManifestVersion, "reads.min");
            run.Equal(reads.GetProperty("maxMajor").GetInt32(), ManifestVersions.NewestSupportedMajor, "reads.maxMajor");
            run.Equal(root.EnumerateObject().Count(), 7, "and no other key");

            using JsonDocument none = JsonDocument.Parse(ConnectedHosts.Announce("2026", "0.2.0", null, "m").ToJson());
            run.True(
                none.RootElement.TryGetProperty("document", out JsonElement value) && value.ValueKind == JsonValueKind.Null,
                "document is present and null when none is open");
        });
    }

    private static void ConnectionIdCases(TestRun run)
    {
        run.Case("a connection id is stable for the process", () =>
        {
            run.True(ReferenceEquals(ConnectionIdSource.ThisLaunch, ConnectionIdSource.ThisLaunch), "one source");
            run.Equal(ConnectionIdSource.ThisLaunch.Id, ConnectionIdSource.ThisLaunch.Id, "the same id every read");

            ConnectionIdSource source = new();
            run.Equal(source.Id, source.Id, "and for any source");
        });

        run.Case("a connection id is fresh across launches", () =>
        {
            // A launch is a new source: nothing is read back from disk, so two never agree.
            HashSet<string> seen = [];
            for (int i = 0; i < 50; i++)
            {
                seen.Add(new ConnectionIdSource().Id);
            }

            run.Equal(seen.Count, 50, "fifty launches, fifty ids");
        });

        run.Case("a connection id is within the contract's bounds and says what it is", () =>
        {
            string id = ConnectionIdSource.ThisLaunch.Id;

            run.True(id.Length is >= 1 and <= 200, "1 to 200 characters");
            run.True(id.StartsWith(ConnectionIdSource.Prefix, StringComparison.Ordinal), "and carries its prefix");
            run.True(id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "and is safe in a URL and a filter");
        });
    }

    private static string CamelExport(string status = "sent", string connection = Connection) => $$"""
        {
          "id": "exp-1",
          "orderId": "ord-1",
          "host": "revit",
          "targetConnectionId": "{{connection}}",
          "target": { "document": "Site.rvt", "machine": "DESK-7", "pluginVersion": "0.2.0", "later": 1 },
          "status": "{{status}}",
          "progress": null,
          "failureReason": null,
          "failureMessage": null,
          "createdAt": "2026-10-27T10:00:00.000Z",
          "updatedAt": "2026-10-27T10:00:01.000Z",
          "endedAt": null,
          "futureField": { "anything": true }
        }
        """;

    private static string SnakeExport(string status = "sent", string connection = Connection) => $$"""
        {
          "id": "exp-1",
          "order_id": "ord-1",
          "host": "revit",
          "target_connection_id": "{{connection}}",
          "target_document": "Site.rvt",
          "target_machine": "DESK-7",
          "target_plugin_version": "0.2.0",
          "status": "{{status}}",
          "progress": null,
          "failure_reason": null,
          "failure_message": null,
          "created_at": "2026-10-27T10:00:00.000Z",
          "updated_at": "2026-10-27T10:00:01.000Z",
          "ended_at": null,
          "future_column": 3
        }
        """;

    private static void ParsingCases(TestRun run)
    {
        run.Case("the route's camelCase and the row change's snake_case read to one value", () =>
        {
            run.True(HostExports.TryParse(CamelExport(), out HostExport fromRoute) is null, "the route's spelling reads");
            run.True(HostExports.TryParse(SnakeExport(), out HostExport fromChange) is null, "the change's spelling reads");

            run.True(fromRoute == fromChange, "and they are the same value");
            run.Equal(fromRoute.Id, "exp-1", "id");
            run.Equal(fromRoute.OrderId, "ord-1", "order id");
            run.Equal(fromRoute.Host, "revit", "host");
            run.Equal(fromRoute.TargetConnectionId, Connection, "target connection id");
            run.Equal(fromRoute.TargetDocument, "Site.rvt", "target document");
            run.Equal(fromRoute.TargetMachine, "DESK-7", "target machine");
            run.Equal(fromRoute.TargetPluginVersion, "0.2.0", "target plugin version");
            run.Equal(fromRoute.Status, ExportStatus.Sent, "status");
            run.True(fromRoute.Progress is null, "no progress outside importing");
            run.True(fromRoute.EndedAt is null, "not ended");
            run.Equal(fromRoute.CreatedAt, "2026-10-27T10:00:00.000Z", "created at, as written");
        });

        run.Case("a progress, a failure and an end read in both spellings", () =>
        {
            const string route = """
                { "id": "e", "orderId": "o", "host": "revit", "targetConnectionId": "c",
                  "target": { "document": null, "machine": null, "pluginVersion": null },
                  "status": "failed", "progress": 40, "failureReason": "no_disk", "failureMessage": "Out of space.",
                  "createdAt": "a", "updatedAt": "b", "endedAt": "c" }
                """;
            const string change = """
                { "id": "e", "order_id": "o", "host": "revit", "target_connection_id": "c",
                  "target_document": null, "target_machine": null, "target_plugin_version": null,
                  "status": "failed", "progress": 40, "failure_reason": "no_disk", "failure_message": "Out of space.",
                  "created_at": "a", "updated_at": "b", "ended_at": "c" }
                """;

            run.True(HostExports.TryParse(route, out HostExport a) is null, "route reads");
            run.True(HostExports.TryParse(change, out HostExport b) is null, "change reads");
            run.True(a == b, "same value");
            run.Equal(a.Progress ?? -1, 40, "progress");
            run.Equal(a.FailureReason, "no_disk", "reason");
            run.Equal(a.FailureMessage, "Out of space.", "message");
            run.Equal(a.EndedAt, "c", "ended at");
            run.True(a.TargetDocument is null, "a null target document is none");
        });

        run.Case("every status the contract names reads, and one it does not is left alone", () =>
        {
            foreach (ExportStatus status in Enum.GetValues<ExportStatus>())
            {
                string wire = ExportStatuses.ToWire(status);
                run.True(HostExports.TryParse(SnakeExport(wire), out HostExport export) is null, $"{wire} reads");
                run.Equal(export.Status, status, $"{wire} is itself");
            }

            run.True(HostExports.TryParse(SnakeExport("exploded"), out _) is not null, "an unknown status is not acted on");
            run.True(HostExports.TryParse("{ not json", out _) is not null, "bad JSON is refused, not thrown");
            run.True(HostExports.TryParse("[]", out _) is not null, "a non-object is refused");
            run.True(HostExports.TryParse("{ \"status\": \"sent\" }", out _) is not null, "a row with no id is refused");
        });

        run.Case("the catch-up list reads every row it understands", () =>
        {
            string list = $$"""{ "exports": [ {{CamelExport()}}, {{CamelExport("exploded")}}, {{CamelExport("received")}} ], "extra": 1 }""";

            run.True(HostExports.TryParseList(list, out IReadOnlyList<HostExport> exports) is null, "reads");
            run.Equal(exports.Count, 2, "the row with a status it does not know is left out");
            run.Equal(exports[1].Status, ExportStatus.Received, "in order");

            run.True(HostExports.TryParseList("{ \"nothing\": [] }", out _) is not null, "no exports key is refused");
            run.True(HostExports.TryParseList("{", out _) is not null, "bad JSON is refused");
        });

        run.Case("an export for another connection id is ignored", () =>
        {
            HostExports.TryParse(CamelExport(connection: "revit-someone-else"), out HostExport other);
            HostExports.TryParse(SnakeExport(), out HostExport mine);

            run.True(mine.IsFor(Connection), "mine is acted on");
            run.False(other.IsFor(Connection), "another connection's is not");
            run.False(mine.IsFor(string.Empty), "and no id matches nothing");
            run.False(mine.IsFor("REVIT-0123456789ABCDEF"), "ids compare exactly");

            IReadOnlyList<HostExport> kept = HostExports.ForConnection([other, mine, other], Connection);
            run.Equal(kept.Count, 1, "only mine survives a filter");

            HostExports.TryParse(SnakeExport().Replace("\"host\": \"revit\"", "\"host\": \"unreal\"", StringComparison.Ordinal), out HostExport unreal);
            run.False(unreal.IsFor(Connection), "and an export for the other host is not Revit's");
        });
    }

    private static ExportTransition Make(string move) => move switch
    {
        "received" => ExportTransitions.Received,
        "waiting_for_bundle" => ExportTransitions.WaitingForBundle,
        "awaiting_options" => ExportTransitions.AwaitingOptions,
        "start" => ExportTransitions.StartImport,
        "progress" => ExportTransitions.ReportProgress(40),
        "imported" => ExportTransitions.Imported,
        "cancelled" => ExportTransitions.Cancelled,
        "failed" => ExportTransitions.Failed("disk_full", "Out of space."),
        _ => throw new ArgumentOutOfRangeException(nameof(move)),
    };

    private static void LifecycleCases(TestRun run)
    {
        string[] moves =
            ["received", "waiting_for_bundle", "awaiting_options", "start", "progress", "imported", "cancelled", "failed"];

        run.Case("every move the contract allows a plugin is allowed, and every other is refused", () =>
        {
            foreach (ExportStatus from in Enum.GetValues<ExportStatus>())
            {
                foreach (string move in moves)
                {
                    bool expected = Array.Exists(Allowed, a => a.From == from && a.Move == move);
                    string? verdict = ExportLifecycle.Check(from, Make(move));

                    run.Equal(verdict is null, expected, $"{ExportStatuses.ToWire(from)} -> {move}: {verdict ?? "allowed"}");
                }
            }
        });

        run.Case("nothing leaves a terminal status", () =>
        {
            foreach (ExportStatus terminal in new[] { ExportStatus.Imported, ExportStatus.Failed, ExportStatus.Cancelled })
            {
                run.True(ExportStatuses.IsTerminal(terminal), $"{terminal} is terminal");
                foreach (string move in moves)
                {
                    run.True(ExportLifecycle.Check(terminal, Make(move)) is not null, $"{terminal} -> {move} refused");
                }
            }

            run.False(ExportStatuses.IsTerminal(ExportStatus.Importing), "importing is not");
        });

        run.Case("a plugin never sends sent", () =>
        {
            run.True(
                ExportLifecycle.Check(ExportStatus.Sent, new ExportTransition(ExportStatus.Sent, null, null, null)) is not null,
                "refused");
        });

        run.Case("an importing report without progress is refused", () =>
        {
            run.True(ExportLifecycle.Check(ExportStatus.Importing, ExportTransitions.StartImport) is not null, "none given");
            run.True(ExportLifecycle.Check(ExportStatus.Importing, ExportTransitions.ReportProgress(0)) is null, "0 is a progress");
            run.True(ExportLifecycle.Check(ExportStatus.Importing, ExportTransitions.ReportProgress(100)) is null, "100 is a progress");
            run.True(ExportLifecycle.Check(ExportStatus.Importing, ExportTransitions.ReportProgress(-1)) is not null, "-1 is out of range");
            run.True(ExportLifecycle.Check(ExportStatus.Importing, ExportTransitions.ReportProgress(101)) is not null, "101 is out of range");
        });

        run.Case("a failure needs a reason that is a code", () =>
        {
            ExportStatus from = ExportStatus.Importing;

            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed("ok_code_1", null)) is null, "a code, no message");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed(new string('a', 64), null)) is null, "64 characters");
            run.True(ExportLifecycle.Check(from, new ExportTransition(ExportStatus.Failed, null, null, "words")) is not null, "no reason");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed(string.Empty, "words")) is not null, "empty reason");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed(new string('a', 65), null)) is not null, "65 characters");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed("Upper", null)) is not null, "upper case");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed("has space", null)) is not null, "a space");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed("dash-ed", null)) is not null, "a dash");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed("The disk is full.", null)) is not null, "free text");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed("café", null)) is not null, "non-ASCII");
            run.True(ExportLifecycle.Check(from, ExportTransitions.Failed("ok\n", null)) is not null, "a trailing newline");
        });

        run.Case("the two codes that belong to the web are refused", () =>
        {
            foreach (string web in new[] { "not_received", "host_disconnected" })
            {
                string? verdict = ExportLifecycle.Check(ExportStatus.Received, ExportTransitions.Failed(web, "x"));
                run.True(verdict is not null, $"{web} refused");
                run.Contains(verdict, "web", $"{web} says whose it is");
            }

            run.Equal(ExportLifecycle.WebOnlyReasons.Count, 2, "and there are two");
        });

        run.Case("a message is the plugin's own words, cut to the contract's limit", () =>
        {
            ExportTransition cut = ExportTransitions.Failed("x_y", new string('m', 5000));

            run.Equal(cut.Message?.Length ?? -1, 1000, "cut to 1000");
            run.True(ExportLifecycle.Check(ExportStatus.Importing, cut) is null, "and allowed");
            run.True(
                ExportLifecycle.Check(
                    ExportStatus.Importing,
                    new ExportTransition(ExportStatus.Failed, null, "x_y", new string('m', 1001))) is not null,
                "a hand-built longer one is refused");
        });

        run.Case("a bundle the plugin cannot read is failed as unsupported_version", () =>
        {
            ExportTransition move = ExportTransitions.UnsupportedVersion("too new");

            run.Equal(move.Status, ExportStatus.Failed, "failed");
            run.Equal(move.Reason, "unsupported_version", "with that code");
            run.Equal(move.Message, "too new", "and the words");
            foreach (ExportStatus from in new[] { ExportStatus.Sent, ExportStatus.Received, ExportStatus.WaitingForBundle, ExportStatus.AwaitingOptions })
            {
                run.True(ExportLifecycle.Check(from, move) is null, $"allowed from {from}");
            }
        });

        run.Case("the request body carries exactly the keys the route accepts", () =>
        {
            static IReadOnlyList<string> Keys(string body)
            {
                using JsonDocument document = JsonDocument.Parse(body);
                return [.. document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
            }

            run.Equal(string.Join(",", Keys(ExportLifecycle.BuildBody(Connection, ExportTransitions.Received))), "connectionId,status", "received");
            run.Equal(string.Join(",", Keys(ExportLifecycle.BuildBody(Connection, ExportTransitions.StartImport))), "connectionId,status", "the first importing call sends no progress");
            run.Equal(string.Join(",", Keys(ExportLifecycle.BuildBody(Connection, ExportTransitions.ReportProgress(0)))), "connectionId,progress,status", "a progress report");
            run.Equal(string.Join(",", Keys(ExportLifecycle.BuildBody(Connection, ExportTransitions.Failed("a_b", "words")))), "connectionId,message,reason,status", "a failure");
            run.Equal(string.Join(",", Keys(ExportLifecycle.BuildBody(Connection, ExportTransitions.Failed("a_b", null)))), "connectionId,reason,status", "a failure with no message");

            using JsonDocument document = JsonDocument.Parse(ExportLifecycle.BuildBody(Connection, ExportTransitions.ReportProgress(55)));
            run.Equal(document.RootElement.GetProperty("status").GetString(), "importing", "wire status");
            run.Equal(document.RootElement.GetProperty("progress").GetInt32(), 55, "progress is a number");
            run.Equal(document.RootElement.GetProperty("connectionId").GetString(), Connection, "connection id");

            using JsonDocument waiting = JsonDocument.Parse(ExportLifecycle.BuildBody(Connection, ExportTransitions.WaitingForBundle));
            run.Equal(waiting.RootElement.GetProperty("status").GetString(), "waiting_for_bundle", "snake_case status");
        });
    }

    private static void PacingCases(TestRun run)
    {
        run.Case("progress reports start at 0 and are paced at one every 2 seconds", () =>
        {
            DateTimeOffset now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
            ImportProgressPacer pacer = new(() => now);

            run.Equal(pacer.Start(), 0, "the import starts at 0");
            run.False(pacer.TryNext(10, out _), "nothing to say at once");

            now += TimeSpan.FromMilliseconds(1999);
            run.False(pacer.TryNext(10, out _), "nor a hair under 2 seconds");

            now += TimeSpan.FromMilliseconds(1);
            run.True(pacer.TryNext(10, out int first), "at 2 seconds it reports");
            run.Equal(first, 10, "the latest figure");

            now += TimeSpan.FromSeconds(1);
            run.False(pacer.TryNext(20, out _), "1 second after, not yet");
            now += TimeSpan.FromSeconds(1);
            run.True(pacer.TryNext(25, out int second), "2 seconds after, yes");
            run.Equal(second, 25, "with the figure as it now is, not as it was asked");
        });

        run.Case("a report that would not move, or would go down, is not sent", () =>
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            ImportProgressPacer pacer = new(() => now);
            pacer.Start();

            now += TimeSpan.FromSeconds(5);
            run.False(pacer.TryNext(0, out _), "still 0");

            run.True(pacer.TryNext(50, out _), "50 goes");
            now += TimeSpan.FromSeconds(5);
            run.False(pacer.TryNext(50, out _), "50 again does not");
            run.False(pacer.TryNext(30, out int kept), "30 does not");
            run.Equal(kept, 50, "and the last figure stands");
        });

        run.Case("a figure past the ends is held to 0 to 100, and nothing is sent before the import starts", () =>
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            ImportProgressPacer pacer = new(() => now);
            now += TimeSpan.FromSeconds(10);
            run.False(pacer.TryNext(50, out _), "before Start");

            pacer.Start();
            now += TimeSpan.FromSeconds(10);
            run.True(pacer.TryNext(250, out int sent), "over 100 reports");
            run.Equal(sent, 100, "as 100");
            run.True(ExportLifecycle.Check(ExportStatus.Importing, ExportTransitions.ReportProgress(sent)) is null, "which the lifecycle allows");
        });
    }

    private static void ReadinessCases(TestRun run)
    {
        run.Case("fulfilled is ready; paid is ready only with its processing phase ready", () =>
        {
            run.True(BundleReadiness.IsReady("fulfilled", null), "fulfilled");
            run.True(BundleReadiness.IsReady("fulfilled", "building"), "fulfilled, whatever the phase");
            run.True(BundleReadiness.IsReady("paid", "ready"), "paid and ready");
            run.False(BundleReadiness.IsReady("paid", null), "paid, no phase");
            run.False(BundleReadiness.IsReady("paid", string.Empty), "paid, empty phase");
            run.False(BundleReadiness.IsReady("paid", "building"), "paid, building");
            run.False(BundleReadiness.IsReady("paid", "Ready"), "the phase compares exactly");
        });

        run.Case("no other status counts, whatever its phase", () =>
        {
            foreach (string status in new[] { "pending", "processing", "refunded", "failed", "expired", "deleted", "withheld", "cancelled", "Fulfilled", string.Empty })
            {
                run.False(BundleReadiness.IsReady(status, "ready"), $"'{status}' with a ready phase");
                run.False(BundleReadiness.IsReady(status, null), $"'{status}'");
            }

            run.False(BundleReadiness.IsReady(null, "ready"), "no status");
        });

        run.Case("an order row decides it, and nothing else on the row can", () =>
        {
            run.True(BundleReadiness.IsReadyRow("""{ "id": "o", "status": "fulfilled" }"""), "a fulfilled row");
            run.True(BundleReadiness.IsReadyRow("""{ "status": "paid", "processing_phase": "ready", "other": 1 }"""), "a paid, ready row");
            run.False(BundleReadiness.IsReadyRow("""{ "status": "paid", "processing_phase": "building" }"""), "a paid, building row");
            run.False(
                BundleReadiness.IsReadyRow("""{ "status": "paid", "processing_phase": "building", "view": { "partial": false }, "url": "https://x" }"""),
                "a download that would have answered 200 does not make it ready");
            run.False(BundleReadiness.IsReadyRow("""{ "processing_phase": "ready" }"""), "a phase alone");
            run.False(BundleReadiness.IsReadyRow("{}"), "an empty row");
            run.False(BundleReadiness.IsReadyRow("[]"), "not a row");
            run.False(BundleReadiness.IsReadyRow("{ nope"), "not JSON");
        });
    }

    private static void UnreadableCases(TestRun run)
    {
        run.Case("a bundle the reader would refuse is failed as unsupported_version before the dialog opens", () =>
        {
            run.True(ExportBundles.CanRead(null), "an unknown version is allowed through, as the web allows it");
            run.True(ExportBundles.CanRead("1.0.0"), "the floor");
            run.True(ExportBundles.CanRead("1.10.0"), "a later 1.x");
            run.False(ExportBundles.CanRead("19"), "the integer pre-history");
            run.False(ExportBundles.CanRead("0.9.0"), "below the floor");
            run.False(ExportBundles.CanRead("99.0.0"), "a major ahead");
            run.False(ExportBundles.CanRead("1.0"), "a partial version");

            ExportTransition? refusal = ExportBundles.RefuseUnreadable("99.0.0");
            run.True(refusal is not null, "a move is made");
            run.Equal(refusal?.Reason, "unsupported_version", "with the reason");
            run.True(!string.IsNullOrEmpty(refusal?.Message), "and words for the curator");
            run.True(
                refusal is { } move && ExportLifecycle.Check(ExportStatus.Received, move) is null,
                "which the lifecycle allows");
            run.True(ExportBundles.RefuseUnreadable("1.0.0") is null, "a readable one makes none");
        });
    }
}
