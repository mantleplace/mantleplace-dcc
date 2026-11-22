using System.Text.Json;

namespace MantlePlace.Core;

public static class PublishedRealtime
{
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
