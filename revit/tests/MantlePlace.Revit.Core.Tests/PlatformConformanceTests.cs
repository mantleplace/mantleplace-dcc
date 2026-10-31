using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MantlePlace.PlatformApi;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Drives the shared corpus' <c>platform</c> and <c>client</c> groups through the generated
/// Platform API client: the Quote, the order refusals and <c>client_update_required</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each case is one exchange: the request a host sends and the answer the platform gives, both
/// held to the served spec by <c>check_manifest_conformance.py</c>. A case is replayed twice over
/// a handler that answers with the case's response. Through <see cref="PlatformTransport"/>, the
/// way every Revit call is sent, it asserts the request: the verb, the route and query, the client
/// header, the bearer, the <c>Idempotency-Key</c> and the body. Through the generated
/// <see cref="PlatformApiClient"/> alone, it asserts the answer is one the generated types read:
/// the success type for a success, the typed <c>ApiException&lt;T&gt;</c> for a refusal, never the
/// untyped exception a body the types cannot hold produces.
/// </para>
/// <para>
/// An order body is sent as the case's JSON, the transport's documented path for a body, rather
/// than serialised from the generated body type: the generated <c>payment</c> enum writes its C#
/// member name (<c>Credit</c>), not the spec's value (<c>credit</c>).
/// </para>
/// <para>
/// Generated type names carry a number that moves when the spec gains an operation, so none is
/// named here: a body type is found from the operation's signature, and a result is read by its
/// JSON names.
/// </para>
/// </remarks>
internal static class PlatformConformanceTests
{
    private const string Base = "https://test.example";

    /// <summary>Stands in for the key a case leaves out, once the client has refused to send without one.</summary>
    private const string PlaceholderKey = "corpus-placeholder-key";

    private static readonly HashSet<string> ConsumedExpectationKeys =
    [
        "status",
        "code",
        "handoffUrl",
        "quotePriceCents",
        "priceCents",
        "coverage",
        "accountFields",
        "creditBalanceCents",
        "freeSlotOpen",
        "minimumVersion",
        "sentVersion",
    ];

    internal static int Run()
    {
        TestRun run = new();

        foreach (string group in new[] { "platform", "client" })
        {
            if (ConformanceCorpus.LoadGroup(group, out List<ConformanceCorpus.CorpusCase> cases) is { } problem)
            {
                run.Fail(problem);
                continue;
            }

            if (cases.Count == 0)
            {
                run.Fail($"the '{group}' group holds no case; a claimed group that runs nothing is a hole (HPS-41)");
            }

            HashSet<string> driven = new(StringComparer.Ordinal);
            foreach (ConformanceCorpus.CorpusCase corpusCase in cases)
            {
                run.Case(corpusCase.Id, () =>
                {
                    Drive(run, corpusCase);
                    driven.Add(corpusCase.Id);
                    foreach (string unasserted in ConformanceCorpus.UnassertedExpectations(corpusCase, ConsumedExpectationKeys))
                    {
                        run.Fail(unasserted);
                    }
                });
            }

            foreach (string undriven in ConformanceCorpus.UndrivenCases(cases, driven))
            {
                run.Fail($"corpus case '{undriven}' loaded but nothing drove it (HPS-41)");
            }
        }

        return run.Report("platform api conformance");
    }

    private static void Drive(TestRun run, ConformanceCorpus.CorpusCase corpusCase)
    {
        using JsonDocument document = JsonDocument.Parse(corpusCase.Payload);
        JsonElement exchange = document.RootElement;
        string operationId = exchange.GetProperty("operationId").GetString()!;
        JsonElement request = exchange.GetProperty("request");
        JsonElement response = exchange.GetProperty("response");
        int status = response.GetProperty("status").GetInt32();
        string answerBody = response.GetProperty("body").GetRawText();

        string? authorization = Header(request, "Authorization");
        string? bearer = authorization?["Bearer ".Length..];
        string clientHeader = Header(request, "Mantle-Place-Client") ?? "revit/0.0.0";
        string? key = Header(request, "Idempotency-Key");
        bool omitsKey = Omits(request, "Idempotency-Key");
        string? requestBody = request.TryGetProperty("body", out JsonElement body) ? body.GetRawText() : null;

        Func<PlatformApiClient, string?, CancellationToken, Task>? operation = operationId switch
        {
            "getQuote" => (api, _, ct) => api.GetQuoteAsync(Query(request, "extent"), ct),
            "placeOrder" => PlaceOrder,
            _ => null,
        };
        if (operation is null)
        {
            run.Fail($"no generated call is mapped to operationId '{operationId}'; map it here");
            return;
        }

        if (omitsKey)
        {
            Recorder refused = new(status, answerBody);
            run.Throws<ArgumentNullException>(
                () => Wait(operation(new PlatformApiClient(new HttpClient(refused)) { BaseUrl = Base }, null, default)),
                "the generated client refuses to send an order without an Idempotency-Key");
            run.Equal(refused.Requests.Count, 0, "and nothing was sent");
            key = PlaceholderKey;
        }

        // The request, as the transport sends it.
        Recorder handler = new(status, answerBody);
        List<ClientUpdateNotice> notices = [];
        PlatformTransport transport = new(Base, clientHeader, handler, notices.Add);
        PlatformAnswer answer = Wait(transport.SendAsync((api, ct) => operation(api, key, ct), bearer, default, requestBody));

        run.Equal(handler.Requests.Count, 1, "one request");
        Seen sent = handler.Requests[0];
        run.Equal(sent.Method, request.GetProperty("method").GetString(), "the verb");
        run.Equal(sent.Url, Base + request.GetProperty("path").GetString() + QueryString(request), "the route and query");
        run.Equal(sent.ClientHeader, clientHeader, "the client header, once");
        run.Equal(sent.Authorization, authorization ?? string.Empty, "the bearer, or none");
        run.Equal(sent.IdempotencyKey, key ?? string.Empty, "the Idempotency-Key, or none");
        if (requestBody is not null)
        {
            run.True(JsonNode.DeepEquals(JsonNode.Parse(sent.Body), JsonNode.Parse(requestBody)), "the body, as the case sends it");
        }

        run.Equal(answer.Status, status, "the answer's status, as sent");
        run.Equal(notices.Count, status == ClientUpdateNotice.Status ? 1 : 0, "the update notice is raised for a 426 client_update_required only");

        // The answer, as the generated types read it.
        (int seenStatus, object? typed) = ReadTyped(operation, key, status, answerBody);
        run.Equal(seenStatus, status, "the generated client saw the status");
        if (typed is null)
        {
            run.Fail($"the generated types could not read the {status} body; the case and the client disagree about the spec");
            return;
        }

        JsonNode read = JsonSerializer.SerializeToNode(typed, typed.GetType())!;
        if (corpusCase.IsAccept)
        {
            run.True(status is >= 200 and < 300, "an accept case is a success");
            AssertQuote(run, corpusCase, read);
        }
        else
        {
            run.True(status >= 400, "a reject case is a refusal");
            AssertRefusal(run, corpusCase, read, seenStatus, notices);
        }
    }

    private static void AssertQuote(TestRun run, ConformanceCorpus.CorpusCase corpusCase, JsonNode quote)
    {
        if (ConformanceCorpus.WantsInt(corpusCase, "priceCents", out int price))
        {
            run.Equal((int)quote["priceCents"]!.GetValue<long>(), price, "priceCents");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "coverage", out string coverage))
        {
            // The generated enum reads the spec's value; written back it is its C# member name.
            run.True(string.Equals(quote["coverage"]?.ToString(), coverage, StringComparison.OrdinalIgnoreCase), "coverage " + coverage);
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "accountFields", out bool accountFields))
        {
            run.Equal(quote["creditBalanceCents"] is not null, accountFields, "creditBalanceCents present only with a valid bearer");
            run.Equal(quote["freeSlotOpen"] is not null, accountFields, "freeSlotOpen present only with a valid bearer");
        }

        if (ConformanceCorpus.WantsInt(corpusCase, "creditBalanceCents", out int balance))
        {
            run.Equal((int)quote["creditBalanceCents"]!.GetValue<long>(), balance, "creditBalanceCents");
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "freeSlotOpen", out bool slot))
        {
            run.Equal(quote["freeSlotOpen"]!.GetValue<bool>(), slot, "freeSlotOpen");
        }
    }

    private static void AssertRefusal(
        TestRun run,
        ConformanceCorpus.CorpusCase corpusCase,
        JsonNode refusal,
        int seenStatus,
        List<ClientUpdateNotice> notices)
    {
        if (ConformanceCorpus.WantsInt(corpusCase, "status", out int status))
        {
            run.Equal(seenStatus, status, "status");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "code", out string code))
        {
            run.Equal(refusal["code"]?.GetValue<string>(), code, "code");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "handoffUrl", out string handoff))
        {
            run.Equal(refusal["handoffUrl"]?.GetValue<string>(), handoff, "handoffUrl");
        }

        if (ConformanceCorpus.WantsInt(corpusCase, "quotePriceCents", out int quotePrice))
        {
            run.Equal((int)(refusal["quote"]?["priceCents"]?.GetValue<long>() ?? -1), quotePrice, "the current Quote's priceCents");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "minimumVersion", out string minimum))
        {
            run.Equal(refusal["minimumVersion"]?.GetValue<string>(), minimum, "minimumVersion");
            run.Equal(notices.FirstOrDefault()?.MinimumVersion, minimum, "the update notice names the minimum");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "sentVersion", out string sentVersion))
        {
            run.Equal(refusal["version"]?.GetValue<string>(), sentVersion, "the version sent, echoed");
            run.Equal(notices.FirstOrDefault()?.Version, sentVersion, "the update notice names the version sent");
        }
    }

    /// <summary>Runs the operation on the generated client alone and returns what its types made of the answer.</summary>
    private static (int Status, object? Typed) ReadTyped(
        Func<PlatformApiClient, string?, CancellationToken, Task> operation,
        string? key,
        int status,
        string answerBody)
    {
        PlatformApiClient api = new(new HttpClient(new Recorder(status, answerBody))) { BaseUrl = Base };
        Task call = operation(api, key, default);
        try
        {
            call.GetAwaiter().GetResult();
            return (status, call.GetType().GetProperty("Result")?.GetValue(call));
        }
        catch (ApiException exception)
        {
            // ApiException<T> carries the body read as the spec's type; a bare ApiException means it could not be.
            object? typed = exception.GetType().IsGenericType
                ? exception.GetType().GetProperty("Result")?.GetValue(exception)
                : null;
            return (exception.StatusCode, typed);
        }
    }

    /// <summary>The generated order call, its body type found from its own signature.</summary>
    private static Task PlaceOrder(PlatformApiClient api, string? key, CancellationToken cancellationToken)
    {
        MethodInfo method = typeof(PlatformApiClient).GetMethods()
            .Single(m => m.Name == nameof(PlatformApiClient.PlaceOrderAsync) && m.GetParameters().Length == 3);
        object body = Activator.CreateInstance(method.GetParameters()[1].ParameterType)!;
        return (Task)method.Invoke(api, [key, body, cancellationToken])!;
    }

    private static string? Header(JsonElement request, string name)
    {
        if (request.TryGetProperty("headers", out JsonElement headers))
        {
            foreach (JsonProperty header in headers.EnumerateObject())
            {
                if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Value.GetString();
                }
            }
        }

        return null;
    }

    private static bool Omits(JsonElement request, string name)
        => request.TryGetProperty("omits", out JsonElement omits)
            && omits.EnumerateArray().Any(item => string.Equals(item.GetString(), name, StringComparison.OrdinalIgnoreCase));

    private static string Query(JsonElement request, string name)
        => request.GetProperty("query").GetProperty(name).GetString()!;

    private static string QueryString(JsonElement request)
    {
        if (!request.TryGetProperty("query", out JsonElement query))
        {
            return string.Empty;
        }

        string joined = string.Join("&", query.EnumerateObject()
            .Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.GetString()!)));
        return joined.Length == 0 ? string.Empty : "?" + joined;
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private sealed record Seen(string Method, string Url, string Authorization, string ClientHeader, string IdempotencyKey, string Body);

    /// <summary>Records each request and answers every one with the case's response.</summary>
    private sealed class Recorder(int status, string body) : HttpMessageHandler
    {
        internal List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string sentBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                Values(request, PlatformClientHeader.Name),
                Values(request, "Idempotency-Key"),
                sentBody));
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        private static string Values(HttpRequestMessage request, string name)
            => request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join("|", values) : string.Empty;
    }
}
