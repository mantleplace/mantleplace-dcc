using System.Text.Json;

namespace MantlePlace.Core;

public static class ClientUpdatePrompt
{
    public const string DownloadUrl = "https://github.com/mantleplace/mantleplace-dcc/releases";

    public const string Title = "Mantle Place needs an update";

    public const int Status = 426;

    public const string Code = "client_update_required";

    public static string Sentence(string minimumVersion)
        => "This version of Mantle Place for Revit is no longer served by mantle.place"
            + (string.IsNullOrWhiteSpace(minimumVersion) ? string.Empty : $" (it needs {minimumVersion.Trim()} or later)")
            + ". Download the update: " + DownloadUrl;

    public static string? MinimumVersionOf(int status, string? body)
    {
        if (status != Status || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("code", out JsonElement code)
                || code.ValueKind != JsonValueKind.String
                || code.GetString() != Code)
            {
                return null;
            }

            return root.TryGetProperty("minimumVersion", out JsonElement minimum) && minimum.ValueKind == JsonValueKind.String
                ? minimum.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
