using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MantlePlace.PlatformApi;

/// <summary>What the platform answered: the status and the body, as sent.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The response body; empty when there was none.</param>
public readonly record struct PlatformAnswer(int Status, string Body);

/// <summary>
/// The platform's answer to a client below the minimum version it serves: HTTP 426 with the code
/// <c>client_update_required</c>. An update prompt, never an error.
/// </summary>
/// <param name="Client">The client name the request sent.</param>
/// <param name="Version">The version the request sent.</param>
/// <param name="MinimumVersion">The lowest version of this client the platform serves.</param>
/// <param name="Message">The platform's own sentence.</param>
public sealed record ClientUpdateNotice(string Client, string Version, string MinimumVersion, string Message)
{
    /// <summary>The status the platform answers with.</summary>
    public const int Status = 426;

    /// <summary>The body's <c>code</c>.</summary>
    public const string Code = "client_update_required";

    /// <summary>
    /// Reads an answer as an update notice. Both halves are required: a bare 426 from a proxy, or the
    /// code on any other status, is an ordinary failure, so this cannot swallow an unrelated error.
    /// </summary>
    public static bool TryRead(int status, string? body, [NotNullWhen(true)] out ClientUpdateNotice? notice)
    {
        notice = null;
        if (status != Status || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "code") != Code)
            {
                return false;
            }

            notice = new ClientUpdateNotice(Text(root, "client"), Text(root, "version"), Text(root, "minimumVersion"), Text(root, "error"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
/// The one place a Platform API request is sent. Every request carries the client header, and the
/// bearer token when there is one; a 426 <c>client_update_required</c> raises the update notice.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route is the generated client's; the answer is the caller's.</b> An operation runs on a
/// <see cref="PlatformApiClient"/>, which builds the path, the verb, the query and the body from the
/// spec. What comes back is the status and the raw body, because the host reads the answer with its
/// own parsers, the ones its conformance corpus pins: a 409 that carries a running job is a success
/// to one caller, and an error body's sentence is what a curator sees. The generated client's own
/// typed result, and the exception it throws for a status it does not call success, are therefore
/// not used.
/// </para>
/// <para>
/// <b>A request body, when the caller has one, is the caller's JSON, sent as it is.</b> The host's
/// bodies are pinned by its own rules (an explicit token list, a transition the lifecycle allows),
/// and the spec's body types cannot all carry them unchanged: a <c>tokens</c> that is either a word
/// or a list has no faithful C# shape, and an optional field the caller leaves out is written as a
/// default. So the generated call is made with an empty body object, and the JSON replaces it on the
/// way out. The route, the verb and the parameters stay the generated client's.
/// </para>
/// <para>
/// No answer at all is the caller's to word: a <see cref="HttpRequestException"/> or a timeout's
/// <see cref="TaskCanceledException"/> propagates exactly as an <see cref="HttpClient"/> throws it.
/// </para>
/// </remarks>
public sealed class PlatformTransport
{
    private static readonly HttpMessageInvoker SharedInvoker = new(new SocketsHttpHandler(), disposeHandler: false);

    private readonly string _baseUrl;
    private readonly HttpMessageInvoker _invoker;
    private readonly Action<ClientUpdateNotice>? _onClientUpdateRequired;

    /// <param name="baseUrl">The platform's base URL (<c>https://mantle.place</c>).</param>
    /// <param name="clientHeader">The <see cref="PlatformClientHeader"/> value every request carries.</param>
    /// <param name="handler">A message handler, for tests; a shared socket handler when <c>null</c>.</param>
    /// <param name="onClientUpdateRequired">Told each time the platform answers 426 <c>client_update_required</c>.</param>
    public PlatformTransport(
        string baseUrl,
        string clientHeader,
        HttpMessageHandler? handler = null,
        Action<ClientUpdateNotice>? onClientUpdateRequired = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientHeader);
        _baseUrl = baseUrl;
        ClientHeader = clientHeader;
        _invoker = handler is null ? SharedInvoker : new HttpMessageInvoker(handler, disposeHandler: false);
        _onClientUpdateRequired = onClientUpdateRequired;
    }

    /// <summary>The header value every request carries.</summary>
    public string ClientHeader { get; }

    /// <summary>Runs one operation and returns what the platform answered.</summary>
    /// <param name="operation">One call on the generated client, e.g. <c>(api, ct) =&gt; api.ListVaultBundlesAsync(ct)</c>.</param>
    /// <param name="bearerToken">The access token, or <c>null</c> for a public route.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <param name="jsonBody">The request body as JSON, for an operation that takes one; <c>null</c> for none.</param>
    public async Task<PlatformAnswer> SendAsync(
        Func<PlatformApiClient, CancellationToken, Task> operation,
        string? bearerToken,
        CancellationToken cancellationToken,
        string? jsonBody = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Capture capture = new(_invoker, ClientHeader, bearerToken, jsonBody);
        using HttpClient http = new(capture, disposeHandler: true);
        PlatformApiClient api = new(http) { BaseUrl = _baseUrl };

        try
        {
            await operation(api, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException) when (capture.Answer is not null)
        {
            // A status the spec does not call success, or a body its types do not hold: either way
            // the answer arrived, and it is the caller's to read.
        }

        PlatformAnswer answer = capture.Answer
            ?? throw new InvalidOperationException("The Platform API operation sent no request.");

        if (ClientUpdateNotice.TryRead(answer.Status, answer.Body, out ClientUpdateNotice? notice))
        {
            _onClientUpdateRequired?.Invoke(notice);
        }

        return answer;
    }

    /// <summary>Stamps the request and keeps the answer, buffered so the generated client can read it too.</summary>
    private sealed class Capture(HttpMessageInvoker inner, string clientHeader, string? bearerToken, string? jsonBody)
        : HttpMessageHandler
    {
        internal PlatformAnswer? Answer { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Remove(PlatformClientHeader.Name);
            request.Headers.TryAddWithoutValidation(PlatformClientHeader.Name, clientHeader);
            if (!string.IsNullOrEmpty(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            if (jsonBody is not null)
            {
                request.Content?.Dispose();
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // ReadAsStringAsync buffers the content, so the generated client's own read sees the same bytes.
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Answer = new PlatformAnswer((int)response.StatusCode, body);
            return response;
        }
    }
}
