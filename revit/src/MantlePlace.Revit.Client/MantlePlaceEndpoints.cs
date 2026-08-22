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

    /// <summary>
    /// The refresh exchange on the web broker — the path that needs no local configuration.
    /// </summary>
    /// <remarks>
    /// Sign-in needs nothing because both mantle.place routes are compiled in; refresh should be no
    /// different. When it was Supabase-direct only, a curator without
    /// <c>%LOCALAPPDATA%\MantlePlace\config.json</c> could sign in once and then lose the session
    /// at access-token expiry with a misconfiguration message they had no way to act on — and that
    /// file has no packaging step that produces it. Supabase-direct is still preferred when the
    /// project URL and anon key ARE configured, so no existing install changes behaviour.
    /// </remarks>
    public string RefreshEndpointUrl { get; init; } = "https://mantle.place/api/v1/auth/native/refresh";

    /// <summary>The platform API the vault client talks to.</summary>
    public string ApiBaseUrl { get; init; } = "https://mantle.place";

    /// <summary>Supabase project URL, for the direct refresh call. Empty until configured.</summary>
    public string SupabaseUrl { get; init; } = string.Empty;

    /// <summary>Supabase anon (public) key. A publishable client key — never a service-role key.</summary>
    public string SupabaseAnonKey { get; init; } = string.Empty;

    /// <summary>
    /// Explicit loopback ports to try, in order (<c>HPS-06</c>). <b>Empty by default, which means
    /// the OS picks the port</b> — see <c>LoopbackRedirectListener.StartEphemeral</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A default list used to live here and was the wrong shape twice over. Windows reserves
    /// ~100-port blocks for Hyper-V/WinNAT that move across reboots, and a bind into one is refused
    /// while nothing is listening — 51000-51009 sat entirely inside one and took sign-in down. A
    /// 512 stride dodged that particular block, but it is still guessing against a moving target,
    /// and it still made every host on the machine draw from one finite list: Revit plus an Unreal
    /// editor plus a second Revit session, with the editor holding its port for the whole process.
    /// A port the OS assigns has neither problem.
    /// </para>
    /// <para>
    /// Set <c>loopbackPorts</c> in <c>config.json</c> to force specific ports — for a site that has
    /// allow-listed them and needs those exact numbers honoured. That is the only reason to.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> LoopbackPorts { get; init; } = [];

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
                RefreshEndpointUrl = Override(root, "refreshEndpointUrl", defaults.RefreshEndpointUrl),
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

    /// <summary>
    /// The Supabase-direct refresh URL, or <c>null</c> when Supabase is not configured — in which
    /// case <see cref="RefreshEndpointUrl"/> is used instead.
    /// </summary>
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
