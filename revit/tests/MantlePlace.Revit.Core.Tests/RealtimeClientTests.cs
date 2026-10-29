using System.Text.Json;
using System.Threading.Channels;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The Realtime client against an in-process loopback WebSocket server: join, track, a row change,
/// a token refresh, a dropped socket, a refused join, an order watched and dropped, and stop (the
/// platform export contract, sections 1 to 3). Nothing leaves the machine (<c>HPS-42</c>).
/// </summary>
internal static class RealtimeClientTests
{
    private const string Connection = "revit-0123456789abcdef";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Long enough that a retry the client should not make has had every chance to show.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private static readonly string Token1 = RealtimeFramesTests.Jwt("""{"sub":"user-1","n":1}""");

    private static readonly string Token2 = RealtimeFramesTests.Jwt("""{"sub":"user-1","n":2}""");

    private static readonly ConnectedHost Presence = ConnectedHosts.Announce("2027", "1.0.0", "Site.rvt", "DESK-7");

    internal static int Run()
    {
        TestRun run = new();

        run.Case("it joins the private channel, tracks presence, and says a catch-up is due", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);

            run.True(rig.Client.Start(Token1, Presence), "started");

            ServerConnection socket = (await server.AcceptAsync(Timeout))!;
            run.Contains(socket.RequestTarget, "/realtime/v1/websocket?apikey=pk-test&vsn=1.0.0", "the location and the key");

            RealtimeMessage join = (await socket.NextAsync(Timeout))!;
            run.Equal(join.Event, "phx_join", "a join");
            run.Equal(join.Topic, "realtime:vault:user-1", "on the curator's topic");
            using (JsonDocument payload = JsonDocument.Parse(join.PayloadJson))
            {
                JsonElement config = payload.RootElement.GetProperty("config");
                run.Equal(config.GetProperty("private").GetBoolean(), true, "private");
                run.Equal(config.GetProperty("presence").GetProperty("key").GetString(), Connection, "keyed by the connection");
                run.Equal(payload.RootElement.GetProperty("access_token").GetString(), Token1, "authorised");
                run.Equal(config.GetProperty("postgres_changes").GetArrayLength(), 1, "exports only");
            }

            run.Equal(rig.Client.State, RealtimeState.Joining, "joining until the reply");
            await socket.ReplyOkAsync(join);

            RealtimeMessage track = (await socket.NextAsync(Timeout))!;
            run.Equal(track.Event, "presence", "a presence push");
            run.Contains(track.PayloadJson, "\"track\"", "a track");
            run.Contains(track.PayloadJson, "\"Site.rvt\"", "with the payload");
            run.Equal(track.JoinRef, join.Ref, "on that join");

            run.Equal(await rig.NextEventAsync(), "subscribed", "a catch-up is due");
            run.Equal(rig.Client.State, RealtimeState.Subscribed, "subscribed");
        }));

        run.Case("a row change is delivered, on the client's own thread", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            (ServerConnection socket, RealtimeMessage join) = await rig.SubscribeAsync(run);

            await socket.SendAsync($$"""
                {"topic":"{{join.Topic}}","event":"postgres_changes","ref":null,
                 "payload":{"ids":[1],"data":{"schema":"public","table":"bundle_exports","type":"INSERT",
                   "record":{"id":"exp-1","order_id":"ord-1","host":"revit","target_connection_id":"{{Connection}}","status":"sent","created_at":"a","updated_at":"b"},
                   "old_record":{ } } } }
                """);

            run.Equal(await rig.NextEventAsync(), "row:bundle_exports:INSERT", "delivered");
            run.Equal(HostExports.TryParse(rig.LastChange!.RecordJson, out HostExport export), null, "the record reads");
            run.Equal(export.Id, "exp-1", "the export");
            run.True(rig.RaisedOffCaller, "raised on the client's thread, not the caller's");
        }));

        run.Case("a token refresh re-authorises the channel, and the next join carries it", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            (ServerConnection socket, RealtimeMessage join) = await rig.SubscribeAsync(run);

            rig.Client.SetAuth(Token2);

            RealtimeMessage token = (await socket.NextAsync(Timeout))!;
            run.Equal(token.Event, "access_token", "re-authorised");
            run.Equal(token.Topic, join.Topic, "on the channel");
            run.Contains(token.PayloadJson, Token2, "with the new token");

            socket.Drop();
            ServerConnection again = (await server.AcceptAsync(Timeout))!;
            RealtimeMessage rejoin = (await again.NextAsync(Timeout))!;
            run.Contains(rejoin.PayloadJson, Token2, "the rejoin uses the refreshed token");
        }));

        run.Case("a dropped socket reconnects under the same connection id and tracks again", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            (ServerConnection socket, _) = await rig.SubscribeAsync(run);

            socket.Drop();

            ServerConnection again = (await server.AcceptAsync(Timeout))!;
            run.True(again is not null, "a new socket");
            RealtimeMessage join = (await again!.NextAsync(Timeout))!;
            run.Equal(join.Event, "phx_join", "joins again");
            run.Contains(join.PayloadJson, "\"key\":\"" + Connection + "\"", "the same connection id");
            await again.ReplyOkAsync(join);

            RealtimeMessage track = (await again.NextAsync(Timeout))!;
            run.Equal(track.Event, "presence", "tracks again");
            run.Contains(track.PayloadJson, "\"track\"", "a track");
            run.Equal(await rig.NextEventAsync(), "subscribed", "and a second catch-up is due");
        }));

        run.Case("a transient channel error reconnects", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            rig.Client.Start(Token1, Presence);

            ServerConnection socket = (await server.AcceptAsync(Timeout))!;
            RealtimeMessage join = (await socket.NextAsync(Timeout))!;
            await socket.ReplyErrorAsync(join, "MissingPartition: Realtime was unable to find the expected messages partition");

            ServerConnection again = (await server.AcceptAsync(Timeout))!;
            run.True(again is not null, "retried");
            RealtimeMessage rejoin = (await again!.NextAsync(Timeout))!;
            run.Equal(rejoin.Event, "phx_join", "joins again");
            await again.ReplyOkAsync(rejoin);
            run.Equal(await rig.NextEventAsync(), "subscribed", "and subscribes");
        }));

        run.Case("a join refused as Unauthorized is not retried", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            rig.Client.Start(Token1, Presence);

            ServerConnection socket = (await server.AcceptAsync(Timeout))!;
            RealtimeMessage join = (await socket.NextAsync(Timeout))!;
            await socket.ReplyErrorAsync(join, "Unauthorized: You do not have permissions to read from this Channel topic: vault:user-1");

            run.Contains(await rig.NextEventAsync(), "refused:Unauthorized", "refused");
            run.Equal(rig.Client.State, RealtimeState.Refused, "and stays refused");
            run.True(await server.AcceptAsync(Quiet) is null, "no second socket");
            run.Equal(server.Connections, 1, "one socket, ever");
            run.Contains(string.Join("\n", rig.Log), "Unauthorized", "the log says why");
        }));

        run.Case("a changed document re-tracks; the same one does not", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            (ServerConnection socket, _) = await rig.SubscribeAsync(run);

            rig.Client.Track(Presence);
            rig.Client.Track(ConnectedHosts.Announce("2027", "1.0.0", null, "DESK-7"));

            RealtimeMessage track = (await socket.NextAsync(Timeout))!;
            run.Equal(track.Event, "presence", "re-tracked");
            run.Contains(track.PayloadJson, "\"document\":null", "with no document");
            run.True(await socket.NextAsync(Quiet) is null, "the unchanged payload sent nothing");
        }));

        run.Case("an order is watched by rejoining with its row, and dropped the same way", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            (ServerConnection socket, RealtimeMessage first) = await rig.SubscribeAsync(run);

            rig.Client.WatchOrder("ord-9");

            RealtimeMessage leave = (await socket.NextAsync(Timeout))!;
            run.Equal(leave.Event, "phx_leave", "the old join is left");
            run.Equal(leave.JoinRef, first.Ref, "the old one");
            RealtimeMessage join = (await socket.NextAsync(Timeout))!;
            run.Equal(join.Event, "phx_join", "and joined again");
            run.Contains(join.PayloadJson, "\"filter\":\"id=eq.ord-9\"", "with the order's row");
            run.Contains(join.PayloadJson, "\"table\":\"orders\"", "on orders");
            await socket.ReplyOkAsync(join);
            run.Equal((await socket.NextAsync(Timeout))!.Event, "presence", "tracked again");
            run.Equal(await rig.NextEventAsync(), "subscribed", "catch-up due");

            await socket.SendAsync($$"""
                {"topic":"{{join.Topic}}","event":"postgres_changes","ref":null,
                 "payload":{"ids":[2],"data":{"schema":"public","table":"orders","type":"UPDATE","record":{"id":"ord-9","status":"fulfilled"},"old_record":{ } } } }
                """);
            run.Equal(await rig.NextEventAsync(), "row:orders:UPDATE", "the order change delivered");

            rig.Client.StopWatchingOrder();
            run.Equal((await socket.NextAsync(Timeout))!.Event, "phx_leave", "left again");
            RealtimeMessage plain = (await socket.NextAsync(Timeout))!;
            run.Equal(plain.Event, "phx_join", "joined again");
            run.False(plain.PayloadJson.Contains("\"orders\"", StringComparison.Ordinal), "without the order");
        }));

        run.Case("stop untracks, leaves and closes, and nothing reconnects", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            (ServerConnection socket, RealtimeMessage join) = await rig.SubscribeAsync(run);

            Task stopping = rig.Client.StopAsync();

            RealtimeMessage untrack = (await socket.NextAsync(Timeout))!;
            run.Equal(untrack.Event, "presence", "a presence push");
            run.Contains(untrack.PayloadJson, "\"untrack\"", "an untrack");
            RealtimeMessage leave = (await socket.NextAsync(Timeout))!;
            run.Equal(leave.Event, "phx_leave", "then a leave");
            run.Equal(leave.JoinRef, join.Ref, "of the join");
            await socket.ReplyOkAsync(leave);

            run.True(await Task.WhenAny(stopping, Task.Delay(Timeout)) == stopping, "stop finishes");
            run.Equal(rig.Client.State, RealtimeState.Stopped, "stopped");
            run.True(await server.AcceptAsync(Quiet) is null, "no reconnect");
            run.False(rig.Client.Start(Token1, Presence), "and a stopped client does not start again");
            run.True(await server.AcceptAsync(Quiet) is null, "still no socket");
        }));

        run.Case("stop finishes even when nothing answers the leave", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);
            await rig.SubscribeAsync(run);

            Task stopping = rig.Client.StopAsync();
            run.True(await Task.WhenAny(stopping, Task.Delay(Timeout)) == stopping, "bounded");
            run.Equal(rig.Client.State, RealtimeState.Stopped, "stopped");
        }));

        run.Case("with no location or key, nothing connects and one log line says why", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            List<string> log = [];
            await using RealtimeClient client = new(string.Empty, string.Empty, Connection, line => { lock (log) { log.Add(line); } });

            run.False(client.Start(Token1, Presence), "not started");
            run.False(client.Start(Token1, Presence), "nor the second time");
            run.Equal(client.State, RealtimeState.Off, "off");
            run.Equal(log.Count, 1, "one log line");
            run.Contains(log[0], "realtimeUrl", "naming what is missing");
            run.True(await server.AcceptAsync(Quiet) is null, "no socket");

            await using RealtimeClient keyless = new(server.Location, string.Empty, Connection, _ => { });
            run.False(keyless.Start(Token1, Presence), "a location without a key is off too");
            run.True(await server.AcceptAsync(Quiet) is null, "still no socket");
        }));

        run.Case("a token with no subject names no channel, and does not connect", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            await using Rig rig = new(server);

            run.False(rig.Client.Start("opaque", Presence), "not started");
            run.Equal(rig.Log.Count, 1, "one log line");
            run.True(await server.AcceptAsync(Quiet) is null, "no socket");
        }));

        run.Case("the endpoints carry the Realtime location and key, empty until configured", () =>
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "mp-realtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            try
            {
                MantlePlaceEndpoints defaults = MantlePlaceEndpoints.Load(Path.Combine(sandbox, "absent.json"));
                run.Equal(defaults.RealtimeUrl, string.Empty, "no compiled-in location");
                run.Equal(defaults.RealtimePublishableKey, string.Empty, "no compiled-in key");

                string config = Path.Combine(sandbox, "config.json");
                File.WriteAllText(config, """{ "realtimeUrl": "wss://realtime.test.example/realtime/v1", "realtimePublishableKey": "pk-1" }""");
                MantlePlaceEndpoints set = MantlePlaceEndpoints.Load(config);
                run.Equal(set.RealtimeUrl, "wss://realtime.test.example/realtime/v1", "the location");
                run.Equal(set.RealtimePublishableKey, "pk-1", "the key");
                run.Equal(set.ApiBaseUrl, defaults.ApiBaseUrl, "the rest stand");
            }
            finally
            {
                Directory.Delete(sandbox, recursive: true);
            }
        });

        run.Case("a local override wins: the published route is not asked, and the socket opens on the override", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            Route route = new(200, PublishedBody("wss://published.test.example/realtime/v1", "pk-published"));
            MantlePlaceEndpoints endpoints = new() { RealtimeUrl = server.Location, RealtimePublishableKey = "pk-override" };

            RealtimeValues values = await RealtimeLocator.ResolveAsync(endpoints, route);
            run.Equal(values.WhyNot, null, "resolved");
            run.Equal(values.Location, server.Location, "the override's location");
            run.Equal(values.PublishableKey, "pk-override", "the override's key");

            await using RealtimeClient client = RealtimeClient.For(endpoints, Connection, _ => { }, route);
            run.True(client.Start(Token1, Presence), "started");
            ServerConnection socket = (await server.AcceptAsync(Timeout))!;
            run.Contains(socket.RequestTarget, "apikey=pk-override", "the override's key opens it");
            run.Equal(route.Requests.Count, 0, "the route was never read");
        }));

        run.Case("an override of only one value does not borrow the other from the platform", () => Sync(async () =>
        {
            Route route = new(200, PublishedBody("wss://published.test.example/realtime/v1", "pk-published"));
            MantlePlaceEndpoints endpoints = new() { RealtimeUrl = "wss://mine.test.example/realtime/v1" };

            RealtimeValues values = await RealtimeLocator.ResolveAsync(endpoints, route);
            run.True(values.WhyNot is not null, "off");
            run.Contains(values.WhyNot!, "realtimePublishableKey", "naming the missing key");
            run.Equal(route.Requests.Count, 0, "the route was never read");

            List<string> log = [];
            await using RealtimeClient client = RealtimeClient.For(endpoints, Connection, line => log.Add(line), route);
            run.False(client.Start(Token1, Presence), "not started");
            run.Equal(log.Count, 1, "one log line");
        }));

        run.Case("with no override, the published location and key open the socket, read from a route with no credential", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            Route route = new(200, PublishedBody(server.Location, "pk-published"));
            MantlePlaceEndpoints endpoints = new() { ApiBaseUrl = "https://platform.test.example/" };
            Logged logged = new();

            await using RealtimeClient client = RealtimeClient.For(endpoints, Connection, logged.Add, route);
            run.True(client.Start(Token1, Presence), "started: the read happens off the caller's thread");

            ServerConnection socket = (await server.AcceptAsync(Timeout))!;
            run.Contains(socket.RequestTarget, "apikey=pk-published", "the published key opens it");
            RealtimeMessage join = (await socket.NextAsync(Timeout))!;
            run.Equal(join.Event, "phx_join", "and joins");

            run.Equal(route.Requests.Count, 1, "one read");
            run.Equal(route.Requests[0].Method, "GET", "a GET");
            run.Equal(route.Requests[0].Url, "https://platform.test.example/api/v1/realtime", "of the one compiled-in route");
            run.Equal(route.Requests[0].Authorization, string.Empty, "carrying no credential");
            run.False(string.Join("\n", logged.Lines).Contains("pk-published", StringComparison.Ordinal), "the key is never logged");
        }));

        run.Case("a route that cannot be reached leaves the host off, with one logged reason and no socket", () => Sync(async () =>
        {
            using LoopbackRealtimeServer server = new();
            Route route = new(200, string.Empty) { Throw = new HttpRequestException("no route to host") };
            Logged logged = new();

            await using RealtimeClient client = RealtimeClient.For(new MantlePlaceEndpoints(), Connection, logged.Add, route);
            run.True(client.Start(Token1, Presence), "started");

            string line = await logged.NextAsync();
            run.Contains(line, "Connected host is off", "off");
            run.Contains(line, "could not be reached", "saying the route was unreachable");
            run.Contains(line, "no route to host", "and why");
            run.Equal(client.State, RealtimeState.Off, "off");
            run.True(await server.AcceptAsync(Quiet) is null, "no socket");
            run.Equal(logged.Lines.Count, 1, "one line");
        }));

        run.Case("an answer that is not a usable pair leaves the host off, with a logged reason", () => Sync(async () =>
        {
            (string Name, int Status, string Body, string Expected)[] answers =
            [
                ("an error status", 503, "{}", "answered 503"),
                ("an empty body", 200, "", "empty"),
                ("not JSON", 200, "<html>maintenance</html>", "not JSON"),
                ("not an object", 200, "[]", "both a url and a publishableKey"),
                ("no key", 200, """{"url":"wss://published.test.example/realtime/v1"}""", "both a url and a publishableKey"),
                ("no url", 200, """{"publishableKey":"pk-published"}""", "both a url and a publishableKey"),
                ("a blank key", 200, """{"url":"wss://published.test.example/realtime/v1","publishableKey":"  "}""", "both a url and a publishableKey"),
                ("a key that is not a string", 200, """{"url":"wss://published.test.example/realtime/v1","publishableKey":7}""", "both a url and a publishableKey"),
                ("a url that is not a socket", 200, """{"url":"not a location","publishableKey":"pk-published"}""", "not a socket location"),
            ];

            foreach ((string name, int status, string body, string expected) in answers)
            {
                Route route = new(status, body);
                RealtimeValues values = await RealtimeLocator.ResolveAsync(new MantlePlaceEndpoints(), route);
                run.True(values.WhyNot is not null, name + ": off");
                run.Contains(values.WhyNot!, expected, name + ": the reason");
                run.Equal(values.Location + values.PublishableKey, string.Empty, name + ": no value taken");
                run.False(values.WhyNot!.Contains("pk-published", StringComparison.Ordinal), name + ": the key is not in the reason");
            }

            using LoopbackRealtimeServer server = new();
            Logged logged = new();
            await using RealtimeClient client = RealtimeClient.For(
                new MantlePlaceEndpoints(), Connection, logged.Add, new Route(200, """{"url":"wss://x.test.example/realtime/v1"}"""));
            run.True(client.Start(Token1, Presence), "started");
            run.Contains(await logged.NextAsync(), "Connected host is off", "the client logs it");
            run.Equal(client.State, RealtimeState.Off, "off");
            run.True(await server.AcceptAsync(Quiet) is null, "no socket");
        }));

        return run.Report("Realtime client");
    }

    private static string PublishedBody(string url, string key)
        => JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = url, ["publishableKey"] = key });

    private static void Sync(Func<Task> body) => body().GetAwaiter().GetResult();

    private sealed record Seen(string Method, string Url, string Authorization);

    /// <summary>The platform's Realtime route, answered in process.</summary>
    private sealed class Route : HttpMessageHandler
    {
        private readonly int _status;
        private readonly string _body;

        internal Route(int status, string body)
        {
            _status = status;
            _body = body;
        }

        internal List<Seen> Requests { get; } = [];

        internal Exception? Throw { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString() ?? string.Empty));
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)_status) { Content = new StringContent(_body) });
        }
    }

    /// <summary>A client's log, with a wait for its next line.</summary>
    private sealed class Logged
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        internal List<string> Lines { get; } = [];

        internal void Add(string line)
        {
            lock (Lines)
            {
                Lines.Add(line);
            }

            _lines.Writer.TryWrite(line);
        }

        internal async Task<string> NextAsync()
        {
            using CancellationTokenSource wait = new(Timeout);
            try
            {
                return await _lines.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return "(no log line)";
            }
        }
    }

    /// <summary>A client on the loopback server, its events and its log as text in arrival order.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
        private readonly int _callerThread = Environment.CurrentManagedThreadId;
        private readonly LoopbackRealtimeServer _server;

        internal Rig(LoopbackRealtimeServer server)
        {
            _server = server;
            Client = new RealtimeClient(
                server.Location,
                "pk-test",
                Connection,
                line =>
                {
                    lock (Log)
                    {
                        Log.Add(line);
                    }
                },
                backoff: _ => TimeSpan.FromMilliseconds(10));
            Client.Subscribed += (_, _) => _events.Writer.TryWrite("subscribed");
            Client.Refused += (_, reason) => _events.Writer.TryWrite("refused:" + reason);
            Client.RowChanged += (_, change) =>
            {
                LastChange = change;
                RaisedOffCaller = Environment.CurrentManagedThreadId != _callerThread;
                _events.Writer.TryWrite($"row:{change.Table}:{change.Type}");
            };
        }

        internal RealtimeClient Client { get; }

        internal List<string> Log { get; } = [];

        internal RealtimeRowChange? LastChange { get; private set; }

        internal bool RaisedOffCaller { get; private set; }

        internal async Task<string> NextEventAsync()
        {
            using CancellationTokenSource wait = new(Timeout);
            try
            {
                return await _events.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return "(no event)";
            }
        }

        /// <summary>Starts, answers the join, reads the track: the client is subscribed.</summary>
        internal async Task<(ServerConnection Socket, RealtimeMessage Join)> SubscribeAsync(TestRun run)
        {
            Client.Start(Token1, Presence);
            ServerConnection socket = (await _server.AcceptAsync(Timeout).ConfigureAwait(false))!;
            RealtimeMessage join = (await socket.NextAsync(Timeout).ConfigureAwait(false))!;
            await socket.ReplyOkAsync(join).ConfigureAwait(false);
            RealtimeMessage track = (await socket.NextAsync(Timeout).ConfigureAwait(false))!;
            run.Equal(track.Event, "presence", "subscribed: tracked");
            run.Equal(await NextEventAsync().ConfigureAwait(false), "subscribed", "subscribed: catch-up due");
            return (socket, join);
        }

        public async ValueTask DisposeAsync() => await Client.DisposeAsync().ConfigureAwait(false);
    }
}
