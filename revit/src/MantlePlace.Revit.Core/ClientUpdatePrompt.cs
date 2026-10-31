using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What the curator is told when the platform no longer serves this build: HTTP 426 with the code
/// <c>client_update_required</c>. An update prompt with the download link, never an error.
/// </summary>
/// <remarks>
/// The platform's answer names the minimum version and no link of its own, so the link is this
/// plugin's: the repository's Releases page, which the README's install section already names as
/// where a build comes from. Moving it (a storefront page) is this one constant.
/// </remarks>
public static class ClientUpdatePrompt
{
    /// <summary>Where the update is downloaded.</summary>
    public const string DownloadUrl = "https://github.com/mantleplace/mantleplace-dcc/releases";

    /// <summary>The prompt's title.</summary>
    public const string Title = "Mantle Place needs an update";

    /// <summary>The status the platform answers with.</summary>
    public const int Status = 426;

    /// <summary>The body's <c>code</c>.</summary>
    public const string Code = "client_update_required";

    /// <summary>The sentence the prompt shows, and the sentence the refused call reports.</summary>
    /// <param name="minimumVersion">The lowest version the platform serves; empty when it did not say.</param>
    public static string Sentence(string minimumVersion)
        => "This version of Mantle Place for Revit is no longer served by mantle.place"
            + (string.IsNullOrWhiteSpace(minimumVersion) ? string.Empty : $" (it needs {minimumVersion.Trim()} or later)")
            + ". Download the update: " + DownloadUrl;

    /// <summary>
    /// Reads an answer as the update answer. Both halves are required: a bare 426 from a proxy, or the
    /// code on another status, is an ordinary refusal.
    /// </summary>
    /// <returns>The minimum version (empty when unstated), or <c>null</c> when this is not the update answer.</returns>
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
