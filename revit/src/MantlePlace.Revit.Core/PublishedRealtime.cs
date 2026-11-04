using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What the platform's public Realtime route answers: where a plugin's socket is and the publishable
/// key that opens it. Read, never invented: a body that does not say both is refused with its reason.
/// </summary>
/// <remarks>
/// <para>
/// The route is public and unauthenticated, and the body is one JSON object with two strings:
/// <c>url</c> (the socket endpoint, <c>wss://…/realtime/v1/websocket</c>, used as it is) and <c>publishableKey</c>. A
/// publishable key is public by design, but it is still never logged, and this type never puts one in
/// a reason.
/// </para>
/// <para>
/// Pure: the fetch is <c>MantlePlace.Revit.Client</c>'s, so every malformed shape is pinned headlessly.
/// </para>
/// </remarks>
public static class PublishedRealtime
{
    /// <summary>
    /// Reads the route's body. <c>true</c> when both values are usable; otherwise <c>false</c> with the
    /// reason in <paramref name="whyNot"/>, in a sentence that names neither value.
    /// </summary>
    public static bool TryParse(string? body, out string location, out string publishableKey, out string whyNot)
    {
        location = string.Empty;
        publishableKey = string.Empty;
        whyNot = string.Empty;

        if (string.IsNullOrWhiteSpace(body))
        {
            whyNot = "the platform's answer was empty";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            whyNot = "the platform's answer was not JSON";
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string? url = root.ValueKind == JsonValueKind.Object ? Text(root, "url") : null;
            string? key = root.ValueKind == JsonValueKind.Object ? Text(root, "publishableKey") : null;

            if (url is null || key is null)
            {
                whyNot = "the platform's answer did not carry both a url and a publishableKey";
                return false;
            }

            if (RealtimeChannel.SocketUrl(url, key) is null)
            {
                whyNot = "the platform's answer carried a url that is not a socket location";
                return false;
            }

            location = url.Trim();
            publishableKey = key.Trim();
            return true;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;
}
