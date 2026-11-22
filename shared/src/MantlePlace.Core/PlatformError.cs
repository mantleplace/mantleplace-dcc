using System.Text.Json;

namespace MantlePlace.Core;

public readonly record struct PlatformError(string Message, string Code, string Detail)
{
    public string Sentence
        => Detail.Length == 0 || Detail == Message ? Message : Message + " — " + Detail;
}

public static class PlatformErrors
{
    private static readonly string[] MessagePrecedence =
        ["error_description", "msg", "message", "error_code", "error"];

    public static bool TryRead(JsonElement root, out PlatformError error)
    {
        error = default;

        foreach (string field in MessagePrecedence)
        {
            string message = root.Str(field);
            if (message.Length > 0)
            {
                error = new PlatformError(message, root.Str("code"), ReadDetail(root));
                return true;
            }
        }

        return false;
    }

    private static string ReadDetail(JsonElement root)
    {
        string detail = root.Str("detail");
        if (detail.Length > 0)
        {
            return detail;
        }

        if (root.Array("issues") is not { } issues)
        {
            return string.Empty;
        }

        List<string> rendered = [];
        foreach (JsonElement issue in issues.EnumerateArray())
        {
            string reason = issue.Str("message");
            if (reason.Length == 0)
            {
                continue;
            }

            string field = string.Join(".", issue.StringArray("path"));
            rendered.Add(field.Length > 0 ? field + ": " + reason : reason);
        }

        return string.Join("; ", rendered);
    }

    public static string? Refusal(int status, string body)
        => status is >= 200 and < 300
            ? null
            : ClientUpdatePrompt.MinimumVersionOf(status, body) is { } minimumVersion
                ? ClientUpdatePrompt.Sentence(minimumVersion)
            : FromBody(body) is { } platformError
                ? platformError.Sentence
                : $"mantle.place refused this request (HTTP {status}) and gave no reason.";

    public static PlatformError? FromBody(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && TryRead(document.RootElement, out PlatformError error)
                    ? error
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
