using System.Text.Json;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Where the plugin talks to, and the loopback range it listens on.
/// </summary>
/// <remarks>
/// <para>
/// The two public mantle.place routes are compiled in: they are public URLs, they change only with
/// a deploy, and a plugin that cannot sign in without a config file is a plugin that cannot sign in.
/// </para>
/// <para>
/// <b><see cref="SupabaseUrl"/> and <see cref="SupabaseAnonKey"/> have no default and are not in
/// this repo.</b> Token REFRESH still goes Supabase-direct — there is no
/// <c>/api/v1/auth/native/refresh</c> — so the plugin needs the project URL and the public anon key
/// the way the Unreal plugin needs them in <c>DefaultGame.ini</c>. They are hydrated into
/// <c>%LOCALAPPDATA%\MantlePlace\config.json</c> at packaging time from the build's secret store,
/// which is the single source of truth for every value of this kind. Absent, sign-in still works and
/// refresh
/// reports a named misconfiguration rather than a bare 401.
/// </para>
/// </remarks>
public sealed class MantlePlaceEndpoints
{
    /// <summary>The hosted native-login page the system browser is sent to.</summary>
    public string WebLoginUrl { get; init; } = "https://mantle.place/auth/native";

    /// <summary>The PKCE code→token exchange.</summary>
    public string TokenEndpointUrl { get; init; } = "https://mantle.place/api/v1/auth/native/token";

    /// <summary>The platform API the vault client talks to.</summary>
    public string ApiBaseUrl { get; init; } = "https://mantle.place";

    /// <summary>Supabase project URL, for the direct refresh call. Empty until configured.</summary>
    public string SupabaseUrl { get; init; } = string.Empty;

    /// <summary>Supabase anon (public) key. A publishable client key — never a service-role key.</summary>
    public string SupabaseAnonKey { get; init; } = string.Empty;

    /// <summary>
    /// Loopback ports tried in order (<c>HPS-06</c>). Ten is enough for a handful of Revit sessions
    /// on one machine and small enough that a firewall exception is a bounded ask.
    /// </summary>
    public IReadOnlyList<int> LoopbackPorts { get; init; } =
        [51000, 51001, 51002, 51003, 51004, 51005, 51006, 51007, 51008, 51009];

    /// <summary>Path component of the loopback redirect URI.</summary>
    public string CallbackPath { get; init; } = "/callback";

    /// <summary>How long to wait for the browser round-trip before cancelling (<c>HPS-09</c>).</summary>
    public int SignInTimeoutSeconds { get; init; } = 300;

    /// <summary>Where the optional overrides live.</summary>
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        "config.json");

    /// <summary>
    /// The compiled defaults, with <see cref="ConfigPath"/> layered over them when it exists.
    /// </summary>
    /// <remarks>
    /// An unreadable or malformed config falls back to the defaults rather than throwing. This runs
    /// during Revit's add-in load, where an exception costs the ribbon button and tells the curator
    /// nothing; a plugin that signs in against production because someone put a trailing comma in a
    /// dev override is the better failure.
    /// </remarks>
    public static MantlePlaceEndpoints Load() => Load(ConfigPath);

    /// <summary>As <see cref="Load()"/>, from an explicit path. For tests.</summary>
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
                ApiBaseUrl = Override(root, "apiBaseUrl", defaults.ApiBaseUrl),
                SupabaseUrl = Override(root, "supabaseUrl", defaults.SupabaseUrl),
                SupabaseAnonKey = Override(root, "supabaseAnonKey", defaults.SupabaseAnonKey),
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

    /// <summary>The refresh URL, or <c>null</c> when Supabase is not configured.</summary>
    public string? RefreshTokenUrl
    {
        get
        {
            string? normalised = MantlePlace.Revit.Core.AuthUrls.NormaliseBaseUrl(SupabaseUrl);
            return normalised is null || SupabaseAnonKey.Length == 0
                ? null
                : normalised + "/auth/v1/token?grant_type=refresh_token";
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
