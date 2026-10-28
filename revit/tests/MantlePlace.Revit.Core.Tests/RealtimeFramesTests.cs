using System.Text;
using System.Text.Json;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The Realtime (Phoenix) frames the connected host speaks, encoded and decoded with no socket: every
/// outgoing frame the export contract needs, and every incoming one it acts on (the platform export
/// contract, sections 1 and 3).
/// </summary>
internal static class RealtimeFramesTests
{
    private const string Topic = "realtime:vault:user-1";
    private const string Connection = "revit-0123456789abcdef";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the channel is the curator's vault topic, prefixed as Realtime names it on the wire", () =>
        {
            run.Equal(RealtimeChannel.Topic("user-1"), "vault:user-1", "the contract's topic");
            run.Equal(RealtimeChannel.WireTopic("user-1"), Topic, "the wire topic");
        });

        run.Case("a join is private, keyed by the connection id, authorised, and subscribes to this connection's exports", () =>
        {
            string frame = RealtimeFrames.Join(Topic, "1", "token-1", Connection, [RealtimeBinding.Exports(Connection)]);

            using JsonDocument doc = JsonDocument.Parse(frame);
            JsonElement root = doc.RootElement;
            run.Equal(root.GetProperty("topic").GetString(), Topic, "topic");
            run.Equal(root.GetProperty("event").GetString(), "phx_join", "event");
            run.Equal(root.GetProperty("ref").GetString(), "1", "ref");
            run.Equal(root.GetProperty("join_ref").GetString(), "1", "join_ref is the join's own ref");

            JsonElement payload = root.GetProperty("payload");
            run.Equal(payload.GetProperty("access_token").GetString(), "token-1", "authorised with the session's token");
            JsonElement config = payload.GetProperty("config");
            run.Equal(config.GetProperty("private").GetBoolean(), true, "private: true");
            run.Equal(config.GetProperty("presence").GetProperty("key").GetString(), Connection, "presence key");
            run.Equal(config.GetProperty("broadcast").GetProperty("self").GetBoolean(), false, "no broadcast to self");

            JsonElement changes = config.GetProperty("postgres_changes");
            run.Equal(changes.GetArrayLength(), 1, "one binding");
            run.Equal(changes[0].GetProperty("event").GetString(), "*", "every change");
            run.Equal(changes[0].GetProperty("schema").GetString(), "public", "public schema");
            run.Equal(changes[0].GetProperty("table").GetString(), "bundle_exports", "the exports table");
            run.Equal(changes[0].GetProperty("filter").GetString(), "target_connection_id=eq." + Connection, "filtered to this connection");
        });

        run.Case("a join that watches an order adds the order row by id", () =>
        {
            string frame = RealtimeFrames.Join(Topic, "4", "t", Connection, [RealtimeBinding.Exports(Connection), RealtimeBinding.Order("ord-9")]);

            using JsonDocument doc = JsonDocument.Parse(frame);
            JsonElement changes = doc.RootElement.GetProperty("payload").GetProperty("config").GetProperty("postgres_changes");
            run.Equal(changes.GetArrayLength(), 2, "two bindings");
            run.Equal(changes[1].GetProperty("table").GetString(), "orders", "the orders table");
            run.Equal(changes[1].GetProperty("filter").GetString(), "id=eq.ord-9", "that one order");
        });

        run.Case("a track carries the presence payload verbatim", () =>
        {
            ConnectedHost host = ConnectedHosts.Announce("2027", "1.0.0", "Site.rvt", "DESK-7");
            string frame = RealtimeFrames.Track(Topic, "1", "2", host.ToJson());

            using JsonDocument doc = JsonDocument.Parse(frame);
            JsonElement root = doc.RootElement;
            run.Equal(root.GetProperty("event").GetString(), "presence", "event");
            run.Equal(root.GetProperty("ref").GetString(), "2", "ref");
            run.Equal(root.GetProperty("join_ref").GetString(), "1", "join_ref");
            JsonElement payload = root.GetProperty("payload");
            run.Equal(payload.GetProperty("type").GetString(), "presence", "type");
            run.Equal(payload.GetProperty("event").GetString(), "track", "track");
            JsonElement entry = payload.GetProperty("payload");
            run.Equal(entry.GetProperty("host").GetString(), "revit", "host");
            run.Equal(entry.GetProperty("document").GetString(), "Site.rvt", "document");
            run.Equal(entry.GetProperty("reads").GetProperty("min").GetString(), host.ReadsMin, "reads.min");
        });

        run.Case("an untrack names no payload", () =>
        {
            using JsonDocument doc = JsonDocument.Parse(RealtimeFrames.Untrack(Topic, "1", "7"));
            JsonElement payload = doc.RootElement.GetProperty("payload");
            run.Equal(doc.RootElement.GetProperty("event").GetString(), "presence", "event");
            run.Equal(payload.GetProperty("type").GetString(), "presence", "type");
            run.Equal(payload.GetProperty("event").GetString(), "untrack", "untrack");
            run.False(payload.TryGetProperty("payload", out _), "no entry");
        });

        run.Case("a leave, a heartbeat and a token refresh", () =>
        {
            using JsonDocument leave = JsonDocument.Parse(RealtimeFrames.Leave(Topic, "1", "8"));
            run.Equal(leave.RootElement.GetProperty("event").GetString(), "phx_leave", "leave event");
            run.Equal(leave.RootElement.GetProperty("join_ref").GetString(), "1", "leave join_ref");

            using JsonDocument beat = JsonDocument.Parse(RealtimeFrames.Heartbeat("9"));
            run.Equal(beat.RootElement.GetProperty("topic").GetString(), "phoenix", "the socket's own topic");
            run.Equal(beat.RootElement.GetProperty("event").GetString(), "heartbeat", "heartbeat event");
            run.Equal(beat.RootElement.GetProperty("payload").ValueKind == JsonValueKind.Object, true, "an empty payload");

            using JsonDocument token = JsonDocument.Parse(RealtimeFrames.AccessToken(Topic, "1", "10", "token-2"));
            run.Equal(token.RootElement.GetProperty("event").GetString(), "access_token", "access_token event");
            run.Equal(token.RootElement.GetProperty("topic").GetString(), Topic, "on the channel");
            run.Equal(token.RootElement.GetProperty("payload").GetProperty("access_token").GetString(), "token-2", "the new token");
        });

        run.Case("a join reply, ok", () =>
        {
            RealtimeMessage message = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"phx_reply","ref":"1","join_ref":"1",
                 "payload":{"status":"ok","response":{"postgres_changes":[{"id":31,"event":"*","schema":"public","table":"bundle_exports"}]} } }
                """);

            run.Equal(message.Kind, RealtimeMessageKind.Reply, "a reply");
            run.True(message.Ok, "ok");
            run.Equal(message.Ref, "1", "ref");
            run.Equal(message.JoinRef, "1", "join_ref");
            run.Equal(message.Topic, Topic, "topic");
        });

        run.Case("a presence reply: the answer to a track, by its ref", () =>
        {
            RealtimeMessage message = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"phx_reply","ref":"2","join_ref":"1","payload":{"status":"ok","response":{ } } }
                """);

            run.Equal(message.Kind, RealtimeMessageKind.Reply, "a reply");
            run.True(message.Ok, "ok");
            run.Equal(message.Ref, "2", "the track's ref");
        });

        run.Case("a refused join is a reply in error, and the reason is read", () =>
        {
            RealtimeMessage message = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"phx_reply","ref":"1","join_ref":"1",
                 "payload":{"status":"error","response":{"reason":"Unauthorized: You do not have permissions to read from this Channel topic: vault:user-1"} } }
                """);

            run.Equal(message.Kind, RealtimeMessageKind.Reply, "a reply");
            run.False(message.Ok, "not ok");
            run.Contains(message.Reason, "Unauthorized", "the reason");
            run.True(RealtimeChannel.IsRefusal(message.Reason), "a refusal: never retried");
        });

        run.Case("a transient channel error: retried, not refused", () =>
        {
            RealtimeMessage reply = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"phx_reply","ref":"1","join_ref":"1",
                 "payload":{"status":"error","response":{"reason":"MissingPartition: Realtime was unable to find the expected messages partition"} } }
                """);
            run.False(reply.Ok, "not ok");
            run.False(RealtimeChannel.IsRefusal(reply.Reason), "transient");

            RealtimeMessage error = RealtimeFrames.Decode($$"""{"topic":"{{Topic}}","event":"phx_error","ref":null,"join_ref":"1","payload":{ } }""");
            run.Equal(error.Kind, RealtimeMessageKind.ChannelError, "phx_error is a channel error");
            run.False(RealtimeChannel.IsRefusal(error.Reason), "and transient");

            RealtimeMessage system = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"system","ref":null,
                 "payload":{"channel":"vault:user-1","extension":"postgres_changes","message":"MissingPartition: Realtime was unable to find the expected messages partition","status":"error"} }
                """);
            run.Equal(system.Kind, RealtimeMessageKind.ChannelError, "a system error is a channel error");
            run.Contains(system.Reason, "MissingPartition", "with its message");
        });

        run.Case("an Unauthorized system error is a refusal too", () =>
        {
            RealtimeMessage system = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"system","ref":null,"payload":{"message":"Unauthorized: private channel","status":"error"} }
                """);
            run.Equal(system.Kind, RealtimeMessageKind.ChannelError, "a channel error");
            run.True(RealtimeChannel.IsRefusal(system.Reason), "refused");
        });

        run.Case("a system ok and presence state are nothing to act on", () =>
        {
            RealtimeMessage ok = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"system","ref":null,"payload":{"extension":"postgres_changes","message":"Subscribed to PostgreSQL","status":"ok"} }
                """);
            run.Equal(ok.Kind, RealtimeMessageKind.Ignored, "system ok");

            RealtimeMessage state = RealtimeFrames.Decode($$"""{"topic":"{{Topic}}","event":"presence_state","ref":null,"payload":{ } }""");
            run.Equal(state.Kind, RealtimeMessageKind.Ignored, "presence_state");

            RealtimeMessage diff = RealtimeFrames.Decode($$"""{"topic":"{{Topic}}","event":"presence_diff","ref":null,"payload":{"joins":{ },"leaves":{ } } }""");
            run.Equal(diff.Kind, RealtimeMessageKind.Ignored, "presence_diff");
        });

        run.Case("a channel close", () =>
        {
            RealtimeMessage close = RealtimeFrames.Decode($$"""{"topic":"{{Topic}}","event":"phx_close","ref":"1","join_ref":"1","payload":{ } }""");
            run.Equal(close.Kind, RealtimeMessageKind.Close, "a close");
            run.Equal(close.JoinRef, "1", "of that join");
        });

        run.Case("a row change on exports reads as the export it carries", () =>
        {
            RealtimeMessage message = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"postgres_changes","ref":null,
                 "payload":{"ids":[31],"data":{"schema":"public","table":"bundle_exports","type":"INSERT","commit_timestamp":"2026-10-27T00:00:00Z","errors":null,
                   "record":{"id":"exp-1","order_id":"ord-1","host":"revit","target_connection_id":"{{Connection}}","target_document":"Site.rvt",
                             "target_machine":"DESK-7","target_plugin_version":"1.0.0","status":"sent","progress":null,"failure_reason":null,
                             "failure_message":null,"created_at":"a","updated_at":"b","ended_at":null},
                   "old_record":{ } } } }
                """);

            run.Equal(message.Kind, RealtimeMessageKind.RowChange, "a row change");
            RealtimeRowChange change = message.Change!;
            run.Equal(change.Table, "bundle_exports", "table");
            run.Equal(change.Type, "INSERT", "type");
            run.True(change.IsExport, "an export change");
            run.Equal(HostExports.TryParse(change.RecordJson, out HostExport export), null, "the record reads as an export");
            run.Equal(export.Id, "exp-1", "its id");
            run.Equal(export.Status, ExportStatus.Sent, "its status");
            run.True(export.IsFor(Connection), "for this connection");
        });

        run.Case("a row change on orders reads through the readiness rule", () =>
        {
            RealtimeMessage message = RealtimeFrames.Decode($$"""
                {"topic":"{{Topic}}","event":"postgres_changes","ref":null,
                 "payload":{"ids":[32],"data":{"schema":"public","table":"orders","type":"UPDATE",
                   "record":{"id":"ord-1","status":"paid","processing_phase":"ready"},
                   "old_record":{"id":"ord-1"} } } }
                """);

            run.Equal(message.Kind, RealtimeMessageKind.RowChange, "a row change");
            RealtimeRowChange change = message.Change!;
            run.Equal(change.Table, "orders", "table");
            run.Equal(change.Type, "UPDATE", "type");
            run.True(change.IsOrder, "an order change");
            run.True(BundleReadiness.IsReadyRow(change.RecordJson), "ready");
            run.Equal(change.OldRecordJson.Length > 0, true, "the old record kept");
        });

        run.Case("a heartbeat reply is a reply on the socket's topic", () =>
        {
            RealtimeMessage message = RealtimeFrames.Decode("""{"topic":"phoenix","event":"phx_reply","ref":"9","payload":{"status":"ok","response":{}}}""");
            run.Equal(message.Kind, RealtimeMessageKind.Reply, "a reply");
            run.Equal(message.Topic, "phoenix", "socket topic");
            run.Equal(message.Ref, "9", "ref");
            run.Equal(message.JoinRef, null, "no join_ref");
        });

        run.Case("a numeric ref reads as text, and garbage is unreadable rather than thrown", () =>
        {
            RealtimeMessage numeric = RealtimeFrames.Decode("""{"topic":"phoenix","event":"phx_reply","ref":12,"payload":{"status":"ok"}}""");
            run.Equal(numeric.Ref, "12", "ref as text");

            run.Equal(RealtimeFrames.Decode("not json").Kind, RealtimeMessageKind.Unreadable, "not JSON");
            run.Equal(RealtimeFrames.Decode("[1,2]").Kind, RealtimeMessageKind.Unreadable, "not an object");
            run.Equal(
                RealtimeFrames.Decode($$"""{"topic":"{{Topic}}","event":"postgres_changes","payload":{"data":{"table":"orders"} } }""").Kind,
                RealtimeMessageKind.Unreadable,
                "a change with no record");
        });

        run.Case("the socket URL: the location's websocket, the publishable key and the protocol version", () =>
        {
            run.Equal(
                RealtimeChannel.SocketUrl("wss://realtime.test.example/realtime/v1", "pk-1"),
                "wss://realtime.test.example/realtime/v1/websocket?apikey=pk-1&vsn=1.0.0",
                "appended");
            run.Equal(
                RealtimeChannel.SocketUrl("https://realtime.test.example/realtime/v1/", "pk 1"),
                "wss://realtime.test.example/realtime/v1/websocket?apikey=pk%201&vsn=1.0.0",
                "https becomes wss, a trailing slash is dropped, the key is encoded");
            run.Equal(
                RealtimeChannel.SocketUrl("ws://127.0.0.1:5000/realtime/v1/websocket", "k"),
                "ws://127.0.0.1:5000/realtime/v1/websocket?apikey=k&vsn=1.0.0",
                "a location already naming the websocket is not doubled");
            run.Equal(RealtimeChannel.SocketUrl("", "k"), null, "no location, no URL");
            run.Equal(RealtimeChannel.SocketUrl("wss://x", " "), null, "no key, no URL");
            run.Equal(RealtimeChannel.SocketUrl("ftp://x", "k"), null, "not a socket location");
        });

        run.Case("off, with a reason, when either the location or the key is missing", () =>
        {
            run.Equal(RealtimeChannel.WhyOff("wss://x", "k"), null, "both set: on");
            run.Contains(RealtimeChannel.WhyOff("", ""), "realtimeUrl", "names the location key");
            run.Contains(RealtimeChannel.WhyOff("", ""), "realtimePublishableKey", "and the key's");
            run.Contains(RealtimeChannel.WhyOff("wss://x", ""), "realtimePublishableKey", "the key alone");
            run.Contains(RealtimeChannel.WhyOff("", "k"), "realtimeUrl", "the location alone");
            run.Contains(RealtimeChannel.WhyOff("ftp://x", "k"), "realtimeUrl", "a location that is not a socket");
        });

        run.Case("the curator's user id is the access token's subject", () =>
        {
            run.Equal(RealtimeChannel.UserIdOf(Jwt("""{"sub":"user-1","role":"authenticated"}""")), "user-1", "sub");
            run.Equal(RealtimeChannel.UserIdOf(Jwt("""{"role":"anon"}""")), null, "no subject");
            run.Equal(RealtimeChannel.UserIdOf("opaque"), null, "not a JWT");
            run.Equal(RealtimeChannel.UserIdOf("a.@@@.c"), null, "not base64url");
            run.Equal(RealtimeChannel.UserIdOf(string.Empty), null, "empty");
        });

        run.Case("backoff climbs, then holds", () =>
        {
            run.Equal((int)RealtimeChannel.Backoff(1).TotalMilliseconds, 1000, "first");
            run.Equal((int)RealtimeChannel.Backoff(2).TotalMilliseconds, 2000, "second");
            run.Equal((int)RealtimeChannel.Backoff(3).TotalMilliseconds, 5000, "third");
            run.Equal((int)RealtimeChannel.Backoff(4).TotalMilliseconds, 10000, "fourth");
            run.Equal((int)RealtimeChannel.Backoff(40).TotalMilliseconds, 10000, "held");
            run.Equal((int)RealtimeChannel.Backoff(0).TotalMilliseconds, 1000, "never below the first");
        });

        return run.Report("Realtime frames");
    }

    /// <summary>An unsigned token with the given claims: the client reads, never verifies, it.</summary>
    internal static string Jwt(string claims)
        => Base64Url("""{"alg":"HS256","typ":"JWT"}""") + "." + Base64Url(claims) + ".sig";

    private static string Base64Url(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
