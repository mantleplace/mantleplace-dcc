using System.Text.Json;
using MantlePlace.Core;

namespace MantlePlace.Client;

public sealed class MantlePlaceEndpoints
{
    public string WebLoginUrl { get; init; } = "https://mantle.place/auth/native?host=" + HostIdentity.Id;

    public string TokenEndpointUrl { get; init; } = "https://mantle.place/api/v1/auth/native/token";

    public string RefreshEndpointUrl { get; init; } = "https://mantle.place/api/v1/auth/native/refresh";

    public string ApiBaseUrl { get; init; } = "https://mantle.place";

    public string RealtimeUrl { get; init; } = string.Empty;

    public string RealtimePublishableKey { get; init; } = string.Empty;

    public IReadOnlyList<int> LoopbackPorts { get; init; } = [];

    public string CallbackPath { get; init; } = "/callback";

    public int SignInTimeoutSeconds { get; init; } = 300;

    public string OverridesFrom { get; init; } = string.Empty;

    public string? SplitSignIn()
    {
        string? login = OriginOrNull(WebLoginUrl);
        string? other = new[] { TokenEndpointUrl, RefreshEndpointUrl }
            .Select(OriginOrNull)
            .FirstOrDefault(origin => origin is not null && login is not null
                && !string.Equals(origin, login, StringComparison.OrdinalIgnoreCase));
        if (login is null || other is null)
        {
            return null;
        }

        string file = OverridesFrom.Length > 0 ? OverridesFrom : ConfigPath;
        return $"Sign-in is split across two servers: the sign-in page is on {login}, but the code it hands back "
            + $"would be redeemed on {other}, which cannot accept a code another server signed. Both are set in {file}. "
            + "Point webLoginUrl, tokenEndpointUrl and refreshEndpointUrl at one server, or delete that file to sign in to mantle.place.";
    }

    public string Unreachable(string url, string reason)
    {
        string host = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Authority : url;
        string sentence = $"Could not reach {host}: {reason}";
        bool isDefault = Uri.TryCreate(new MantlePlaceEndpoints().ApiBaseUrl, UriKind.Absolute, out Uri? platform)
            && string.Equals(host, platform.Authority, StringComparison.OrdinalIgnoreCase);
        return isDefault || OverridesFrom.Length == 0
            ? sentence
            : $"{sentence} {host} is set in {OverridesFrom}: start that server, or delete the file to use mantle.place.";
    }

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        "config.json");

    public static MantlePlaceEndpoints Load() => Load(ConfigPath);

    public static MantlePlaceEndpoints Load(string configPath)
    {
        MantlePlaceEndpoints defaults = new();
        if (!File.Exists(configPath))
        {
            return defaults;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return defaults;
            }

            return new MantlePlaceEndpoints
            {
                WebLoginUrl = Override(root, "webLoginUrl", defaults.WebLoginUrl),
                TokenEndpointUrl = Override(root, "tokenEndpointUrl", defaults.TokenEndpointUrl),
                RefreshEndpointUrl = Override(root, "refreshEndpointUrl", defaults.RefreshEndpointUrl),
                ApiBaseUrl = Override(root, "apiBaseUrl", defaults.ApiBaseUrl),
                RealtimeUrl = Override(root, "realtimeUrl", defaults.RealtimeUrl),
                RealtimePublishableKey = Override(root, "realtimePublishableKey", defaults.RealtimePublishableKey),
                LoopbackPorts = OverridePorts(root, defaults.LoopbackPorts),
                CallbackPath = Override(root, "callbackPath", defaults.CallbackPath),
                SignInTimeoutSeconds = OverrideInt(root, "signInTimeoutSeconds", defaults.SignInTimeoutSeconds),
                OverridesFrom = configPath,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return defaults;
        }
    }

    private static string? OriginOrNull(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.GetLeftPart(UriPartial.Authority) : null;

    private static string Override(JsonElement root, string key, string fallback)
        => root.TryGetProperty(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
                ? text
                : fallback;

    private static int OverrideInt(JsonElement root, string key, int fallback)
        => root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static IReadOnlyList<int> OverridePorts(JsonElement root, IReadOnlyList<int> fallback)
    {
        if (!root.TryGetProperty("loopbackPorts", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        List<int> ports = [];
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number)
            {
                ports.Add(element.GetInt32());
            }
        }

        return ports.Count > 0 ? ports : fallback;
    }
}
