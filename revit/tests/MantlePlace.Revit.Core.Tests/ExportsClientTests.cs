using System.Net;
using System.Net.Http;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The two v1 export routes and the contract's refusal handling, driven through a canned HTTP
/// handler and a fake bearer session, so no request leaves the machine (<c>HPS-42</c>).
/// </summary>
internal static class ExportsClientTests
{
    private const string Connection = "revit-0123456789abcdef";

    private const string ExportBody = """
        { "id": "exp-1", "orderId": "ord-1", "host": "revit", "targetConnectionId": "revit-0123456789abcdef",
          "target": { "document": "Site.rvt", "machine": "DESK-7", "pluginVersion": "0.2.0" },
          "status": "received", "progress": null, "failureReason": null, "failureMessage": null,
          "createdAt": "a", "updatedAt": "b", "endedAt": null }
        """;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a 200 lists the exports for the connection, on the bearer session", () =>
        {
            Rig rig = new(Answer(200, $$"""{ "exports": [ {{ExportBody}} ] }"""));

            ExportCall<IReadOnlyList<HostExport>> call = Wait(rig.Client.ListAsync(Connection, default));

            run.Equal(call.Outcome, ExportCallOutcome.Ok, "ok");
            run.Equal(call.Value?.Count ?? -1, 1, "one export");
            run.Equal(call.Value![0].Id, "exp-1", "and it is read");
            run.Equal(rig.Handler.Requests.Count, 1, "one request");
            run.Equal(rig.Handler.Requests[0].Method, "GET", "a GET");
            run.Equal(rig.Handler.Requests[0].Url, "https://test.example/api/v1/exports?connectionId=" + Connection, "to the list route");
            run.Equal(rig.Handler.Requests[0].Authorization, "Bearer token-1", "with the bearer token");
        });

        run.Case("a connection id is encoded into the query", () =>
        {
            Rig rig = new(Answer(200, """{ "exports": [] }"""));

            Wait(rig.Client.ListAsync("a b&c=d", default));

            run.Contains(rig.Handler.Requests[0].Url, "connectionId=a%20b%26c%3Dd", "percent-encoded");
        });

        run.Case("a 200 advances one export and reads the updated row", () =>
        {
            Rig rig = new(Answer(200, ExportBody));

            ExportCall<HostExport> call = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.Sent), Connection, ExportTransitions.Received, default));

            run.Equal(call.Outcome, ExportCallOutcome.Ok, "ok");
            run.Equal(call.Value.Status, ExportStatus.Received, "the platform's row is the answer");
            run.Equal(rig.Handler.Requests[0].Method, "POST", "a POST");
            run.Equal(rig.Handler.Requests[0].Url, "https://test.example/api/v1/exports/exp-1/advance", "to the advance route");
            run.Contains(rig.Handler.Requests[0].Body, "\"status\":\"received\"", "with the status");
            run.Contains(rig.Handler.Requests[0].Body, "\"connectionId\":\"" + Connection + "\"", "and the connection id");
        });

        run.Case("a move the contract refuses is not sent", () =>
        {
            Rig rig = new(Answer(200, ExportBody));

            ExportCall<HostExport> call = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.Imported), Connection, ExportTransitions.Received, default));

            run.Equal(call.Outcome, ExportCallOutcome.RefusedLocally, "refused here");
            run.True(call.IsFault, "and it is a plugin bug");
            run.Equal(rig.Handler.Requests.Count, 0, "no request");

            ExportCall<HostExport> web = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.Importing), Connection, ExportTransitions.Failed("not_received", "x"), default));
            run.Equal(web.Outcome, ExportCallOutcome.RefusedLocally, "a web-only code is not sent either");
            run.Equal(rig.Handler.Requests.Count, 0, "still no request");
        });

        run.Case("a 401 refreshes once and retries", () =>
        {
            Rig rig = new(Answer(401, """{ "error": "Invalid or expired token" }"""), Answer(200, """{ "exports": [] }"""));

            ExportCall<IReadOnlyList<HostExport>> call = Wait(rig.Client.ListAsync(Connection, default));

            run.Equal(call.Outcome, ExportCallOutcome.Ok, "ok on the retry");
            run.Equal(rig.Session.Refreshes, 1, "one refresh");
            run.Equal(rig.Handler.Requests.Count, 2, "two requests");
            run.Equal(rig.Handler.Requests[0].Authorization, "Bearer token-1", "the first with the old token");
            run.Equal(rig.Handler.Requests[1].Authorization, "Bearer token-2", "the second with the new one");
        });

        run.Case("a 401 again after the refresh signs in again, and does not loop", () =>
        {
            Rig rig = new(Answer(401, """{ "error": "Unauthenticated" }"""), Answer(401, """{ "error": "Unauthenticated" }"""), Answer(200, "{}"));

            ExportCall<IReadOnlyList<HostExport>> call = Wait(rig.Client.ListAsync(Connection, default));

            run.Equal(call.Outcome, ExportCallOutcome.SignInNeeded, "sign in again");
            run.True(call.IsFault, "a fault");
            run.Equal(call.Status, 401, "status kept");
            run.Equal(rig.Session.Refreshes, 1, "refreshed once");
            run.Equal(rig.Handler.Requests.Count, 2, "asked twice, no third time");
        });

        run.Case("a 401 with nothing to renew signs in without a refresh", () =>
        {
            Rig rig = new(Answer(401, """{ "error": "Unauthenticated" }"""));
            rig.Session.Renewable = false;

            ExportCall<IReadOnlyList<HostExport>> call = Wait(rig.Client.ListAsync(Connection, default));

            run.Equal(call.Outcome, ExportCallOutcome.SignInNeeded, "sign in");
            run.Equal(rig.Session.Refreshes, 0, "no refresh");
            run.Equal(rig.Handler.Requests.Count, 1, "one request");
        });

        run.Case("a failed refresh signs in", () =>
        {
            Rig rig = new(Answer(401, """{ "error": "Invalid or expired token" }"""), Answer(200, "{}"));
            rig.Session.RefreshSucceeds = false;

            ExportCall<IReadOnlyList<HostExport>> call = Wait(rig.Client.ListAsync(Connection, default));

            run.Equal(call.Outcome, ExportCallOutcome.SignInNeeded, "sign in");
            run.Equal(rig.Handler.Requests.Count, 1, "no retry");
        });

        run.Case("no session at all asks for a sign-in without a request", () =>
        {
            Rig rig = new(Answer(200, "{}"));
            rig.Session.Token = string.Empty;
            rig.Session.Renewable = false;

            ExportCall<IReadOnlyList<HostExport>> call = Wait(rig.Client.ListAsync(Connection, default));

            run.Equal(call.Outcome, ExportCallOutcome.SignInNeeded, "sign in");
            run.Equal(rig.Handler.Requests.Count, 0, "nothing sent");
        });

        run.Case("a 409 invalid_transition is the web having moved the export first, not a fault", () =>
        {
            Rig rig = new(Answer(409, """{ "error": "Cannot move", "code": "invalid_transition" }"""));

            ExportCall<HostExport> call = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.AwaitingOptions), Connection, ExportTransitions.StartImport, default));

            run.Equal(call.Outcome, ExportCallOutcome.WebMovedFirst, "web moved it first");
            run.False(call.IsFault, "not a fault");
            run.Contains(call.Message, "web moved", "and says so");
            run.Equal(call.Code, "invalid_transition", "the code is kept");
            run.Equal(rig.Handler.Requests.Count, 1, "and is not retried");
        });

        run.Case("a 429 waits retryAfterMs and tries again", () =>
        {
            Rig rig = new(Answer(429, """{ "error": "Slow down", "code": "rate_limited", "retryAfterMs": 1500 }"""), Answer(200, ExportBody));

            ExportCall<HostExport> call = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.Sent), Connection, ExportTransitions.Received, default));

            run.Equal(call.Outcome, ExportCallOutcome.Ok, "ok after the wait");
            run.Equal(rig.Delays.Count, 1, "waited once");
            run.Equal((int)rig.Delays[0].TotalMilliseconds, 1500, "for retryAfterMs");
            run.Equal(rig.Handler.Requests.Count, 2, "two requests");
        });

        run.Case("a 429 that keeps coming is reported after the waits are spent", () =>
        {
            string limited = """{ "error": "Slow down", "code": "rate_limited", "retryAfterMs": 40 }""";
            Rig rig = new(Answer(429, limited), Answer(429, limited), Answer(429, limited), Answer(200, ExportBody));

            ExportCall<HostExport> call = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.Sent), Connection, ExportTransitions.Received, default));

            run.Equal(call.Outcome, ExportCallOutcome.RateLimited, "rate limited");
            run.Equal(call.RetryAfterMs, 40, "with the wait asked for");
            run.Equal(rig.Delays.Count, ExportsClient.MaxRateLimitRetries, "waited the allowed times");
            run.Equal(rig.Handler.Requests.Count, ExportsClient.MaxRateLimitRetries + 1, "and no more requests");
        });

        run.Case("a 429 with no usable wait gets a default, and a huge one is capped", () =>
        {
            Rig bare = new(Answer(429, """{ "error": "Slow down", "code": "rate_limited" }"""), Answer(429, """{ "retryAfterMs": -5 }"""), Answer(200, "{ \"exports\": [] }"));
            Wait(bare.Client.ListAsync(Connection, default));
            run.Equal((int)bare.Delays[0].TotalMilliseconds, ExportRefusals.DefaultRetryAfterMs, "none stated");
            run.Equal((int)bare.Delays[1].TotalMilliseconds, ExportRefusals.DefaultRetryAfterMs, "a negative one");

            Rig huge = new(Answer(429, """{ "retryAfterMs": 9999999 }"""), Answer(200, "{ \"exports\": [] }"));
            Wait(huge.Client.ListAsync(Connection, default));
            run.Equal((int)huge.Delays[0].TotalMilliseconds, ExportsClient.MaxRetryAfterMs, "capped");
        });

        run.Case("an unknown 4xx code is not retried", () =>
        {
            Rig rig = new(Answer(418, """{ "error": "Teapot", "code": "short_and_stout" }"""), Answer(200, ExportBody));

            ExportCall<HostExport> call = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.Sent), Connection, ExportTransitions.Received, default));

            run.Equal(call.Outcome, ExportCallOutcome.Rejected, "rejected");
            run.True(call.IsFault, "a fault");
            run.Equal(call.Code, "short_and_stout", "the code is kept");
            run.Equal(rig.Handler.Requests.Count, 1, "one request");
            run.Equal(rig.Delays.Count, 0, "no wait");
        });

        run.Case("the known 4xx codes are rejected by class, 409 with another code included", () =>
        {
            foreach ((int status, string code) in new[] { (400, "invalid_body"), (400, "invalid_query"), (403, "wrong_target"), (403, "forbidden"), (404, "not_found"), (409, "something_new") })
            {
                Rig rig = new(Answer(status, $$"""{ "error": "no", "code": "{{code}}" }"""));
                ExportCall<HostExport> call = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.Sent), Connection, ExportTransitions.Received, default));

                run.Equal(call.Outcome, ExportCallOutcome.Rejected, $"{status} {code}");
                run.Equal(rig.Handler.Requests.Count, 1, $"{status} {code} is not retried");
            }
        });

        run.Case("an unknown 5xx code is retry-later, not retried here", () =>
        {
            Rig rig = new(Answer(503, """{ "error": "Down", "code": "brand_new_code" }"""), Answer(200, ExportBody));

            ExportCall<HostExport> call = Wait(rig.Client.AdvanceAsync(Export(ExportStatus.Sent), Connection, ExportTransitions.Received, default));

            run.Equal(call.Outcome, ExportCallOutcome.RetryLater, "retry later");
            run.True(call.IsFault, "a fault");
            run.Equal(call.Status, 503, "status kept");
            run.Equal(rig.Handler.Requests.Count, 1, "the caller owns when to ask again");

            Rig internalError = new(Answer(500, """{ "error": "Oops", "code": "internal_error" }"""));
            run.Equal(Wait(internalError.Client.ListAsync(Connection, default)).Outcome, ExportCallOutcome.RetryLater, "500 internal_error");

            Rig noBody = new(Answer(502, "<html>bad gateway</html>"));
            run.Equal(Wait(noBody.Client.ListAsync(Connection, default)).Outcome, ExportCallOutcome.RetryLater, "a 502 proxy page");
        });

        run.Case("no network, a timeout and a body that is not an export are retry-later", () =>
        {
            Rig down = new();
            down.Handler.Throw = new HttpRequestException("no route to host");
            ExportCall<IReadOnlyList<HostExport>> unreachable = Wait(down.Client.ListAsync(Connection, default));
            run.Equal(unreachable.Outcome, ExportCallOutcome.RetryLater, "unreachable");
            run.Contains(unreachable.Message, "Could not reach", "says so");

            Rig garbled = new(Answer(200, "<html>"));
            run.Equal(Wait(garbled.Client.ListAsync(Connection, default)).Outcome, ExportCallOutcome.RetryLater, "a 200 that is not the list");
        });

        run.Case("the refusal reader tells each class apart on its own", () =>
        {
            run.Equal(ExportRefusals.Classify(401, """{ "error": "Unauthenticated" }""").Kind, ExportRefusalKind.SignInNeeded, "401 by status alone");
            run.Equal(ExportRefusals.Classify(401, string.Empty).Kind, ExportRefusalKind.SignInNeeded, "401 with no body");
            run.Equal(ExportRefusals.Classify(409, """{ "code": "invalid_transition" }""").Kind, ExportRefusalKind.WebMovedFirst, "409 invalid_transition");
            run.Equal(ExportRefusals.Classify(429, """{ "retryAfterMs": 250 }""").RetryAfterMs, 250, "429 retryAfterMs");
            run.Equal(ExportRefusals.Classify(500, "").Kind, ExportRefusalKind.RetryLater, "500");
            run.Equal(ExportRefusals.Classify(404, "").Kind, ExportRefusalKind.Rejected, "404");
            run.False(ExportRefusals.Classify(409, """{ "code": "invalid_transition" }""").IsFault, "the web having moved first is not a fault");
        });

        return run.Report("exports client");
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static (int Status, string Body) Answer(int status, string body) => (status, body);

    private static HostExport Export(ExportStatus status) => new(
        "exp-1", "ord-1", "revit", Connection, "Site.rvt", "DESK-7", "0.2.0", status, null, null, null, "a", "b", null);

    private sealed class Rig
    {
        internal Rig(params (int Status, string Body)[] answers)
        {
            Handler = new CannedHandler(answers);
            Session = new FakeSession();
            Client = new ExportsClient(
                new MantlePlaceEndpoints { ApiBaseUrl = "https://test.example" },
                Session,
                Handler,
                (span, _) =>
                {
                    Delays.Add(span);
                    return Task.CompletedTask;
                });
        }

        internal CannedHandler Handler { get; }

        internal FakeSession Session { get; }

        internal ExportsClient Client { get; }

        internal List<TimeSpan> Delays { get; } = [];
    }

    private sealed class FakeSession : IBearerSession
    {
        private int _generation = 1;

        internal string? Token { get; set; }

        internal bool Renewable { get; set; } = true;

        internal bool RefreshSucceeds { get; set; } = true;

        internal int Refreshes { get; private set; }

        public string AccessToken => Token ?? "token-" + _generation;

        public bool CanRenewSession => Renewable;

        public Task<bool> RefreshAsync(CancellationToken cancellationToken)
        {
            Refreshes++;
            if (RefreshSucceeds)
            {
                _generation++;
                Token = null;
            }

            return Task.FromResult(RefreshSucceeds);
        }
    }

    private sealed record Seen(string Method, string Url, string Authorization, string Body);

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Queue<(int Status, string Body)> _answers;

        internal CannedHandler((int Status, string Body)[] answers)
        {
            _answers = new Queue<(int, string)>(answers);
        }

        internal List<Seen> Requests { get; } = [];

        internal Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString() ?? string.Empty, body));
            if (Throw is not null)
            {
                throw Throw;
            }

            (int status, string answer) = _answers.Count > 0 ? _answers.Dequeue() : (500, "{}");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(answer) };
        }
    }
}
