using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MantlePlace.PlatformApi;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The generated Platform API client and the Revit calls made through it: the client header on every
/// request, the 426 update prompt, and the requests the plugin sent before, sent unchanged.
/// </summary>
internal static class PlatformApiTests
{
    private const string Base = "https://test.example";

    private const string Connection = "revit-0123456789abcdef";

    private const string UpdateBody = """
        { "error": "This version of the Revit plugin is no longer served.", "code": "client_update_required",
          "client": "revit", "version": "1.0.0", "minimumVersion": "1.2.0" }
        """;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the client header is revit and the plugin's version, without build metadata", () =>
        {
            run.Equal(PlatformClientHeader.Name, "Mantle-Place-Client", "the header's name");
            run.Equal(PlatformClientHeader.For("revit", "1.0.0+a36fcd8-dirty"), "revit/1.0.0", "the sha and dirty marker dropped");
            run.Equal(PlatformClientHeader.For("revit", "2.1.0-beta.1"), "revit/2.1.0-beta.1", "a pre-release kept");
            run.Equal(PlatformClientHeader.For("revit", null), "revit/unknown", "no version");
            run.Equal(PlatformClientHeader.For("revit", "v1.0"), "revit/unknown", "not semver");
            run.Equal(PlatformClientHeader.For("revit", "1.0.0 beta"), "revit/unknown", "free text is never sent");

            Version built = typeof(VaultClient).Assembly.GetName().Version!;
            run.Equal(PlatformApiCalls.ClientHeader, $"revit/{built.Major}.{built.Minor}.{built.Build}", "the Revit client sends its own build's version");
        });

        run.Case("every operation the spec serves carries the client header", () =>
        {
            Recorder handler = new();
            PlatformTransport transport = new(Base, "revit/9.8.7", handler);
            MethodInfo[] operations = typeof(PlatformApiClient)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.Name.EndsWith("Async", StringComparison.Ordinal)
                    && method.GetParameters().LastOrDefault()?.ParameterType == typeof(CancellationToken))
                .ToArray();

            run.Equal(operations.Length, SpecOperationCount(), "one generated call per operation in the vendored spec");
            foreach (MethodInfo operation in operations)
            {
                object?[] arguments = operation.GetParameters().Select(Dummy).ToArray();
                Wait(transport.SendAsync((api, _) => (Task)operation.Invoke(api, arguments)!, "token-1", default));
            }

            run.Equal(handler.Requests.Count, operations.Length, "each sent one request");
            run.True(handler.Requests.All(seen => seen.ClientHeader == "revit/9.8.7"), "every one carried the header, once");
            run.True(handler.Requests.All(seen => seen.Url.StartsWith(Base + "/api/v1/", StringComparison.Ordinal)), "on the platform's routes");
        });

        run.Case("an update notice needs both the 426 and the client_update_required code", () =>
        {
            run.True(ClientUpdateNotice.TryRead(426, UpdateBody, out ClientUpdateNotice? notice), "read");
            run.Equal(notice!.MinimumVersion, "1.2.0", "the minimum");
            run.Equal(notice.Version, "1.0.0", "the version sent");
            run.False(ClientUpdateNotice.TryRead(426, "<html>upgrade required</html>", out _), "a bare 426 from a proxy");
            run.False(ClientUpdateNotice.TryRead(400, UpdateBody, out _), "the code on another status");
            run.False(ClientUpdateNotice.TryRead(426, """{ "code": "something_else" }""", out _), "another code");
        });

        run.Case("the transport hands back the answer as sent, whatever the generated types make of it", () =>
        {
            Recorder handler = new((418, "teapot"), (200, "<html>"), (426, UpdateBody));
            List<ClientUpdateNotice> notices = [];
            PlatformTransport transport = new(Base, "revit/1.0.0", handler, notices.Add);

            PlatformAnswer odd = Wait(transport.SendAsync((api, ct) => api.ListVaultBundlesAsync(ct), "t", default));
            run.Equal(odd.Status, 418, "a status the spec never names");
            run.Equal(odd.Body, "teapot", "with its body");

            PlatformAnswer garbled = Wait(transport.SendAsync((api, ct) => api.ListVaultBundlesAsync(ct), "t", default));
            run.Equal(garbled.Status, 200, "a 200 the types cannot hold");
            run.Equal(garbled.Body, "<html>", "is still the caller's to read");
            run.Equal(notices.Count, 0, "neither is an update");

            PlatformAnswer update = Wait(transport.SendAsync((api, ct) => api.ListVaultBundlesAsync(ct), "t", default));
            run.Equal(update.Status, 426, "the update answer");
            run.Equal(notices.Count, 1, "raised the notice once");
            run.Equal(handler.Requests[0].Authorization, "Bearer t", "the bearer token rides along");

            Recorder down = new() { Throw = new HttpRequestException("no route to host") };
            run.Throws<HttpRequestException>(
                () => Wait(new PlatformTransport(Base, "revit/1.0.0", down).SendAsync((api, ct) => api.ListVaultBundlesAsync(ct), null, default)),
                "no answer at all is the caller's to word");
        });

        run.Case("the vault calls send the header, the bearer and the bodies Core builds, unchanged", () =>
        {
            Recorder handler = new(
                (200, Grant("token-1")),
                (200, """{ "bundles": [] }"""),
                (200, "{}"), (200, "{}"), (200, "{}"), (200, "{}"));
            (AuthSession session, VaultClient vault) = SignedIn(handler);

            Wait(vault.ListAsync(default));
            Wait(vault.StartMaterializeAsync("ord 1", MaterializeJobs.HostScope, default));
            Wait(vault.StartMaterializeAsync("ord 1", MaterializeJobs.AllScope, default));
            Wait(vault.PresignAsync("ord 1", default));
            Wait(vault.PollOnceAsync("ord 1", ["x"], default));

            Seen refresh = handler.Requests[0];
            run.Equal(refresh.Url, Base + "/api/v1/auth/native/refresh", "the refresh grant");
            run.True(SameJson(refresh.Body, """{ "refresh_token": "stored-refresh" }"""), "its body");
            run.Equal(refresh.Authorization, string.Empty, "a grant carries no bearer");

            run.Equal(handler.Requests[1].Url, Base + "/api/v1/vault/bundles", "the listing");
            run.Equal(handler.Requests[1].Method, "GET", "a GET");
            run.Equal(handler.Requests[1].Authorization, "Bearer token-1", "on the session's token");

            run.Equal(handler.Requests[2].Url, Base + "/api/v1/vault/bundles/ord%201/materialize", "the materialize route, the id escaped");
            run.True(SameJson(handler.Requests[2].Body, MaterializeJobs.BuildRequestBody(MaterializeJobs.HostScope)), "this host's explicit token list (HPS-23)");
            run.True(SameJson(handler.Requests[3].Body, MaterializeJobs.BuildRequestBody(MaterializeJobs.AllScope)), "or all");
            run.Equal(handler.Requests[4].Url, Base + "/api/v1/vault/bundles/ord%201/download", "the presign route");
            run.True(SameJson(handler.Requests[4].Body, PresignedDownloads.BuildRequestBody()), "the whole-bundle presign body");
            run.Equal(handler.Requests[5].Method, "GET", "the poll is a GET");

            run.True(handler.Requests.All(seen => seen.ClientHeader == PlatformApiCalls.ClientHeader), "every call carried the client header");
            session.Dispose();
        });

        run.Case("the view download sends the body Core builds", () =>
        {
            Recorder handler = new((200, Grant("token-1")), (500, "{}"));
            (AuthSession session, VaultClient vault) = SignedIn(handler);
            string cacheRoot = Path.Combine(Path.GetTempPath(), "mantleplace-platform-api-" + Guid.NewGuid().ToString("N"));
            try
            {
                VaultBundle bundle = new() { OrderId = "ord-1" };
                Wait(vault.DownloadCompleteAsync(bundle, null, new BundleCache(cacheRoot), new Progress<string>(), default));
                run.True(SameJson(handler.Requests[1].Body, HostViews.BuildRequestBody()), "the Revit view body");
                run.Equal(handler.Requests[1].ClientHeader, PlatformApiCalls.ClientHeader, "with the header");
            }
            finally
            {
                session.Dispose();
                if (Directory.Exists(cacheRoot))
                {
                    Directory.Delete(cacheRoot, recursive: true);
                }
            }
        });

        run.Case("the export calls and the Realtime route carry the header, with their routes unchanged", () =>
        {
            Recorder handler = new((200, """{ "exports": [] }"""), (200, "{}"), (200, """{ "url": "wss://r.example/realtime/v1", "publishableKey": "pk" }"""));
            ExportsClient exports = new(new MantlePlaceEndpoints { ApiBaseUrl = Base }, new StaticSession(), handler);
            HostExport current = new("exp 1", "ord-1", "revit", Connection, "Site.rvt", "DESK", "0.2.0", ExportStatus.Sent, null, null, null, "a", "b", null);

            Wait(exports.ListAsync(Connection, default));
            Wait(exports.AdvanceAsync(current, Connection, ExportTransitions.Received, default));
            RealtimeValues realtime = Wait(RealtimeLocator.ResolveAsync(new MantlePlaceEndpoints { ApiBaseUrl = Base }, handler));

            run.Equal(handler.Requests[0].Url, Base + "/api/v1/exports?connectionId=" + Connection, "the list route");
            run.Equal(handler.Requests[1].Url, Base + "/api/v1/exports/exp%201/advance", "the advance route, the id escaped");
            run.True(SameJson(handler.Requests[1].Body, ExportLifecycle.BuildBody(Connection, ExportTransitions.Received)), "the body Core builds");
            run.Equal(handler.Requests[2].Url, Base + "/api/v1/realtime", "the Realtime route");
            run.Equal(handler.Requests[2].Authorization, string.Empty, "which is public: no credential");
            run.Equal(realtime.Location, "wss://r.example/realtime/v1", "and it is read");
            run.True(handler.Requests.All(seen => seen.ClientHeader == PlatformApiCalls.ClientHeader), "every call carried the client header");
        });

        run.Case("a 426 client_update_required surfaces as the update prompt, never an error", () =>
        {
            List<ClientUpdateNotice> raised = [];
            void Listen(object? sender, ClientUpdateNotice notice) => raised.Add(notice);
            PlatformApiCalls.ClientUpdateRequired += Listen;
            try
            {
                Recorder handler = new((200, Grant("token-1")), (426, UpdateBody), (426, UpdateBody));
                (AuthSession session, VaultClient vault) = SignedIn(handler);

                (VaultListing? listing, string? error) = Wait(vault.ListAsync(default));
                run.True(listing is null, "nothing listed");
                run.Equal(error, ClientUpdatePrompt.Sentence("1.2.0"), "the call says update, not refused");
                run.Contains(error, ClientUpdatePrompt.DownloadUrl, "with the download link");
                run.Equal(raised.Count, 1, "and the prompt is raised");
                run.Equal(raised[0].MinimumVersion, "1.2.0", "naming the minimum");

                AuthOutcome renewed = Wait(session.RefreshAsync(default));
                run.False(renewed.Succeeded, "a refresh refused for the version does not renew");
                run.True(session.CanRenewSession, "but the stored session is kept: an update, not a revocation");
                run.Equal(raised.Count, 2, "and it raises the prompt too");
                session.Dispose();

                run.Equal(PlatformErrors.Refusal(426, UpdateBody), ClientUpdatePrompt.Sentence("1.2.0"), "the refusal reader says it too");
                run.Contains(PlatformErrors.Refusal(426, "<html>"), "HTTP 426", "a bare 426 stays an ordinary refusal");

                Recorder exportHandler = new((426, UpdateBody));
                ExportCall<IReadOnlyList<HostExport>> call = Wait(
                    new ExportsClient(new MantlePlaceEndpoints { ApiBaseUrl = Base }, new StaticSession(), exportHandler).ListAsync(Connection, default));
                run.False(call.Succeeded, "the export list does not succeed");
                run.Equal(raised.Count, 3, "and raises the prompt");

                run.Contains(ClientUpdatePrompt.Title, "update", "the prompt's title says update");
                run.True(ClientUpdatePrompt.DownloadUrl.StartsWith("https://", StringComparison.Ordinal), "the link is https");
            }
            finally
            {
                PlatformApiCalls.ClientUpdateRequired -= Listen;
            }
        });

        run.Case("no hand-written route code is left in the Revit client", () =>
        {
            string? folder = RepoTree.Find("revit/src/MantlePlace.Revit.Client");
            run.True(folder is not null, "the client's source is found");
            foreach (string file in Directory.GetFiles(folder!, "*.cs"))
            {
                string name = Path.GetFileName(file);
                string text = File.ReadAllText(file);

                // MantlePlaceEndpoints holds the configured defaults a curator may override, not a call.
                if (name != "MantlePlaceEndpoints.cs")
                {
                    run.False(text.Contains("/api/v1/", StringComparison.Ordinal), name + " builds no Platform API path");
                }

                run.False(text.Contains("new HttpRequestMessage", StringComparison.Ordinal), name + " sends no request of its own");
                run.False(text.Contains("HttpRequestMessage request = new", StringComparison.Ordinal), name + " builds no request by hand");
            }
        });

        return run.Report("platform api");
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static bool SameJson(string actual, string expected)
        => JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));

    private static string Grant(string accessToken)
        => $$"""{ "access_token": "{{accessToken}}", "refresh_token": "stored-refresh", "expires_in": 3600, "user": { "email": "c@example.com" } }""";

    /// <summary>A session renewed from a stored refresh token, through the handler, and a vault on it.</summary>
    private static (AuthSession Session, VaultClient Vault) SignedIn(Recorder handler)
    {
        MantlePlaceEndpoints endpoints = new()
        {
            ApiBaseUrl = Base,
            TokenEndpointUrl = Base + "/api/v1/auth/native/token",
            RefreshEndpointUrl = Base + "/api/v1/auth/native/refresh",
        };
        MemoryStore store = new();
        store.Save("refresh-token", "stored-refresh");
        AuthSession session = new(endpoints, store, handler);
        Wait(session.RestoreAsync(default));
        return (session, new VaultClient(endpoints, session, handler));
    }

    /// <summary>How many operations the vendored spec serves.</summary>
    private static int SpecOperationCount()
    {
        string? generated = RepoTree.Find("revit/src/MantlePlace.PlatformApi/Generated");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(generated!, "openapi.json")));
        string[] verbs = ["get", "put", "post", "delete", "patch"];
        return document.RootElement.GetProperty("paths").EnumerateObject()
            .Sum(path => path.Value.EnumerateObject().Count(member => verbs.Contains(member.Name)));
    }

    /// <summary>An argument a generated call accepts: a string, a body, or the token.</summary>
    private static object? Dummy(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;
        if (type == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        if (type == typeof(string))
        {
            return parameter.Name == "extent" ? "-122.4,37.7,-122.39,37.71" : "x";
        }

        return Activator.CreateInstance(type);
    }

    private sealed record Seen(string Method, string Url, string Authorization, string ClientHeader, string Body);

    private sealed class Recorder : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _answers;

        internal Recorder(params (int Status, string Body)[] answers)
        {
            _answers = new Queue<(int, string)>(answers);
        }

        internal List<Seen> Requests { get; } = [];

        internal Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string header = request.Headers.TryGetValues(PlatformClientHeader.Name, out IEnumerable<string>? values)
                ? string.Join("|", values)
                : string.Empty;
            Requests.Add(new Seen(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                header,
                body));
            if (Throw is not null)
            {
                throw Throw;
            }

            (int status, string answer) = _answers.Count > 0 ? _answers.Dequeue() : (200, "{}");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(answer) };
        }
    }

    private sealed class StaticSession : IBearerSession
    {
        public string AccessToken => "token-1";

        public bool CanRenewSession => false;

        public Task<bool> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class MemoryStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = [];

        public bool IsPersistent => true;

        public bool Save(string key, string secret)
        {
            _secrets[key] = secret;
            return true;
        }

        public bool TryLoad(string key, out string secret) => _secrets.TryGetValue(key, out secret!);

        public void Clear(string key) => _secrets.Remove(key);
    }
}
