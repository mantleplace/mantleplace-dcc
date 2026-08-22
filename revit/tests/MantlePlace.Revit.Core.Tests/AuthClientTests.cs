using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The impure half of the auth triad — secret store, endpoint config, loopback listener.
/// </summary>
/// <remarks>
/// These run on the hosted runner because <c>MantlePlace.Revit.Client</c> references no Revit API.
/// The platform split is asserted rather than assumed: on Linux the suite proves the <c>HPS-16</c>
/// null store degrades honestly, which is the branch a Windows-only suite would never reach.
/// </remarks>
internal static class AuthClientTests
{
    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-auth-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            RunCases(run, sandbox);
        }
        finally
        {
            TryDelete(sandbox);
        }

        return run.Report("auth client");
    }

    private static void RunCases(TestRun run, string sandbox)
    {
        run.Case("the null store fails honestly rather than downgrading (HPS-16)", () =>
        {
            NullSecretStore store = new();
            run.False(store.IsPersistent, "and says so, so the UI can warn about the next session");
            run.False(store.Save("refresh-token", "secret"), "Save refuses");
            run.False(store.TryLoad("refresh-token", out string loaded), "Load finds nothing");
            run.Equal(loaded, string.Empty, "and hands back nothing");
            store.Clear("refresh-token");
        });

        run.Case("this platform gets the store it can honestly provide", () =>
        {
            ISecretStore store = SecretStores.ForCurrentPlatform();
            run.Equal(
                store.IsPersistent,
                OperatingSystem.IsWindows(),
                "DPAPI on Windows, memory-only everywhere else");
        });

        if (OperatingSystem.IsWindows())
        {
            RunWindowsCases(run, sandbox);
        }

        run.Case("the compiled defaults point at production and need no config file", () =>
        {
            MantlePlaceEndpoints endpoints = MantlePlaceEndpoints.Load(Path.Combine(sandbox, "absent.json"));
            run.Equal(endpoints.WebLoginUrl, "https://mantle.place/auth/native", "web login");
            run.Equal(endpoints.TokenEndpointUrl, "https://mantle.place/api/v1/auth/native/token", "token exchange");
            // No declared ports at all: the OS picks one (HPS-06). Every fixed list this shipped
            // with was eventually swallowed by a Windows reserved block that moved under it.
            run.Equal(endpoints.LoopbackPorts.Count, 0, "no declared loopback ports by default");
            run.Equal(endpoints.SignInTimeoutSeconds, 300, "sign-in timeout");
            run.Equal(
                endpoints.RefreshEndpointUrl,
                "https://mantle.place/api/v1/auth/native/refresh",
                "refresh reaches the broker with nothing on disk");
            run.True(
                endpoints.RefreshTokenUrl is null,
                "and the Supabase-direct route stays unconfigured until Supabase is set");
        });

        run.Case("a config file overrides, and a broken one does not take the plugin down", () =>
        {
            string good = Path.Combine(sandbox, "good.json");
            File.WriteAllText(
                good,
                """
                {
                  "webLoginUrl": "https://dev.example/auth/native",
                  "supabaseUrl": "https://ref.supabase.co/",
                  "supabaseAnonKey": "anon",
                  "loopbackPorts": [52000, 52001]
                }
                """);

            MantlePlaceEndpoints overridden = MantlePlaceEndpoints.Load(good);
            run.Equal(overridden.WebLoginUrl, "https://dev.example/auth/native", "overridden");
            run.Equal(
                overridden.TokenEndpointUrl,
                "https://mantle.place/api/v1/auth/native/token",
                "an unmentioned key keeps its default");
            run.Equal(overridden.LoopbackPorts.Count, 2, "ports overridden");
            run.Equal(
                overridden.RefreshTokenUrl,
                "https://ref.supabase.co/auth/v1/token?grant_type=refresh_token",
                "the trailing slash is normalised away rather than doubling");

            // This runs during Revit's add-in load. Throwing there costs the ribbon button and tells
            // the curator nothing.
            string broken = Path.Combine(sandbox, "broken.json");
            File.WriteAllText(broken, "{ not json");
            run.Equal(
                MantlePlaceEndpoints.Load(broken).WebLoginUrl,
                "https://mantle.place/auth/native",
                "a malformed config falls back to the defaults");
        });

        run.Case("a hostless Supabase URL is refused rather than DNS-failing later", () =>
        {
            string path = Path.Combine(sandbox, "hostless.json");
            File.WriteAllText(path, """{ "supabaseUrl": "https:", "supabaseAnonKey": "anon" }""");
            run.True(
                MantlePlaceEndpoints.Load(path).RefreshTokenUrl is null,
                "'https:' would concatenate into the hostless 'https:/auth/v1/token'");
        });

        run.Case("the loopback listener binds before anything opens a browser (HPS-06)", () =>
        {
            using LoopbackRedirectListener? first = LoopbackRedirectListener.StartEphemeral("/callback");
            run.True(first is not null, "bound a port");
            run.True(first!.Port > 0, "and a real one");
            run.Equal(
                first.RedirectUri,
                AuthUrls.BuildLoopbackRedirectUri(first.Port, "/callback"),
                "the redirect URI names the port it actually bound");
            run.Contains(first.RedirectUri, "127.0.0.1", "the literal loopback IP, never 'localhost'");

            // The whole point of returning null rather than throwing: the caller must be able to
            // decide NOT to open a browser that would redirect into nothing.
            using LoopbackRedirectListener? second = LoopbackRedirectListener.Start([first.Port], "/callback");
            run.True(second is null, "a declared list with no free port yields no listener");
        });

        run.Case("concurrent sessions each get their own port", () =>
        {
            // The requirement the old fixed list could not meet: Revit and Unreal signed in at once,
            // or two Revit sessions, or two editors -- all drawing from one five-entry list, with the
            // Unreal editor holding its port for the life of the process. Eight is deliberately more
            // than that list ever had, so this case fails against the range-based implementation.
            List<LoopbackRedirectListener> held = [];
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    LoopbackRedirectListener? listener = LoopbackRedirectListener.StartEphemeral("/callback");
                    run.True(listener is not null, $"session {i} bound a port");
                    held.Add(listener!);
                }

                run.Equal(held.Select(l => l.Port).Distinct().Count(), 8, "eight sessions, eight distinct ports");
            }
            finally
            {
                foreach (LoopbackRedirectListener listener in held)
                {
                    listener.Dispose();
                }
            }
        });

        run.Case("a port lost between probe and bind is retried with a different one", () =>
        {
            // The probe closes its socket before the real listener binds, so another process can
            // take the port in between. The recovery is to propose a FRESH port, not to retry the
            // same number -- retrying the same one just re-runs the lost race.
            using LoopbackRedirectListener? squatter = LoopbackRedirectListener.StartEphemeral("/callback");
            run.True(squatter is not null, "something is holding a port");

            int probes = 0;
            using LoopbackRedirectListener? recovered = LoopbackRedirectListener.StartEphemeral(
                "/callback",
                () =>
                {
                    probes++;
                    // First proposal is already taken; after that, behave like the real probe.
                    return probes == 1 ? squatter!.Port : FreeEphemeralPort();
                });

            run.True(recovered is not null, "the retry recovered");
            run.True(probes >= 2, "and it did so by proposing again, not by reusing the taken port");
            run.True(recovered!.Port != squatter!.Port, "landing on a different port");
        });

        run.Case("an exhausted probe fails without opening a browser", () =>
        {
            using LoopbackRedirectListener? squatter = LoopbackRedirectListener.StartEphemeral("/callback");
            int probes = 0;
            using LoopbackRedirectListener? never = LoopbackRedirectListener.StartEphemeral(
                "/callback",
                () => { probes++; return squatter!.Port; },
                attempts: 3);

            run.True(never is null, "no listener");
            run.Equal(probes, 3, "and it gave up after exactly the attempts it was allowed");
        });

        run.Case("a callback is classified in the HPS-08 precedence", () =>
        {
            // An explicit error beats a state mismatch beats a missing code. Checking state first
            // would report "possible CSRF" for an ordinary "user declined".
            AuthCallback declined = new() { Error = "access_denied", ErrorDescription = "User denied", State = "wrong" };
            run.Equal(
                AuthCallbackQuery.Classify(declined, "right", out string declinedMessage).ToString(),
                CallbackOutcome.ServerError.ToString(),
                "an explicit error wins");
            run.Equal(declinedMessage, "User denied", "and its description is the message");

            AuthCallback forged = new() { Code = "stolen", State = "attacker" };
            run.Equal(
                AuthCallbackQuery.Classify(forged, "ours", out _).ToString(),
                CallbackOutcome.StateMismatch.ToString(),
                "a mismatched state beats a present code");

            AuthCallback empty = new() { State = "ours" };
            run.Equal(
                AuthCallbackQuery.Classify(empty, "ours", out _).ToString(),
                CallbackOutcome.MissingCode.ToString(),
                "and a matching state with no code is a missing code");

            AuthCallback good = new() { Code = "abc", State = "ours" };
            run.Equal(
                AuthCallbackQuery.Classify(good, "ours", out string none).ToString(),
                CallbackOutcome.Code.ToString(),
                "a good callback");
            run.Equal(none, string.Empty, "carries no message");
        });

        run.Case("the browser page escapes in the order that does not double-escape", () =>
        {
            // & first. Escaping < first would produce &lt; and the later & pass would turn it into
            // &amp;lt;, which renders as literal "&lt;" on the page.
            run.Equal(BrowserPages.HtmlEscape("<a & b>"), "&lt;a &amp; b&gt;", "escaped once, correctly");

            string page = BrowserPages.Error("<script>alert(1)</script>");
            run.False(page.Contains("<script>", StringComparison.Ordinal), "the server's message cannot inject markup");
            run.Contains(page, "&lt;script&gt;", "it is shown as text");
            run.False(page.Contains("http://", StringComparison.Ordinal), "the page is self-contained");
            run.False(page.Contains("https://", StringComparison.Ordinal), "no external stylesheet to fail to load");
        });
    }

    /// <summary>
    /// The DPAPI half. Called only under an <see cref="OperatingSystem.IsWindows"/> guard; the
    /// attribute is what tells CA1416 so, because the analyzer does not carry a caller's guard
    /// across a method boundary.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunWindowsCases(TestRun run, string sandbox)
    {
        string root = Path.Combine(sandbox, "dpapi");

        run.Case("DPAPI round-trips the refresh token", () =>
        {
            DpapiSecretStore store = new(root);
            run.True(store.IsPersistent, "and says it persists");
            run.True(store.Save("refresh-token", "r-secret"), "saved");
            run.True(store.TryLoad("refresh-token", out string loaded), "loaded");
            run.Equal(loaded, "r-secret", "round-tripped");
        });

        run.Case("the blob on disk is not the secret", () =>
        {
            DpapiSecretStore store = new(root);
            store.Save("plaintext-check", "the-refresh-token");
            string file = Path.Combine(root, "plaintext-check.bin");
            run.True(File.Exists(file), "a file was written");
            byte[] bytes = File.ReadAllBytes(file);
            run.False(
                System.Text.Encoding.UTF8.GetString(bytes).Contains("the-refresh-token", StringComparison.Ordinal),
                "plaintext on disk is never an option (HPS-14)");
        });

        run.Case("a blob that will not decrypt reads as absence, not as an error (HPS-17)", () =>
        {
            DpapiSecretStore store = new(root);
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "corrupted.bin"), [0x00, 0x01, 0x02, 0x03]);

            // A blob written by a different OS user is indistinguishable from a corrupt one, and
            // both are indistinguishable from absence to the curator.
            run.False(store.TryLoad("corrupted", out string loaded), "no session");
            run.Equal(loaded, string.Empty, "and nothing handed back");
        });

        run.Case("sign-out clears the store", () =>
        {
            DpapiSecretStore store = new(root);
            store.Save("to-clear", "secret");
            store.Clear("to-clear");
            run.False(store.TryLoad("to-clear", out _), "cleared");
            store.Clear("to-clear");
            run.True(true, "clearing twice is not an error");
        });

        run.Case("a storage key becomes a file name only after HPS-30 sanitisation", () =>
        {
            DpapiSecretStore store = new(root);
            run.True(store.Save("../../escaped", "secret"), "saved");
            run.False(
                File.Exists(Path.Combine(root, "..", "..", "escaped.bin")),
                "the traversal never reached the filesystem");
            run.True(store.TryLoad("../../escaped", out string loaded), "and it reads back");
            run.Equal(loaded, "secret", "round-tripped through the sanitised name");
        });
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A port the OS says is free right now. Mirrors the listener's own probe.</summary>
    private static int FreeEphemeralPort()
    {
        System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
        probe.ExclusiveAddressUse = true;
        probe.Start();
        try
        {
            return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}
