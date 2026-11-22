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
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return defaults;
        }
    }

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
