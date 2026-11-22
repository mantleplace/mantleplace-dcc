using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MantlePlace.PlatformApi;

public readonly record struct PlatformAnswer(int Status, string Body);

public sealed record ClientUpdateNotice(string Client, string Version, string MinimumVersion, string Message)
{
    public const int Status = 426;

    public const string Code = "client_update_required";

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

public sealed class PlatformTransport
{
    private static readonly HttpMessageInvoker SharedInvoker = new(new SocketsHttpHandler(), disposeHandler: false);

    private readonly string _baseUrl;
    private readonly HttpMessageInvoker _invoker;
    private readonly Action<ClientUpdateNotice>? _onClientUpdateRequired;

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

    public string ClientHeader { get; }

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
        }

        PlatformAnswer answer = capture.Answer
            ?? throw new InvalidOperationException("The Platform API operation sent no request.");

        if (ClientUpdateNotice.TryRead(answer.Status, answer.Body, out ClientUpdateNotice? notice))
        {
            _onClientUpdateRequired?.Invoke(notice);
        }

        return answer;
    }

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

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Answer = new PlatformAnswer((int)response.StatusCode, body);
            return response;
        }
    }
}
