using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>The outcome of a sign-in, refresh or restore.</summary>
/// <param name="Succeeded">Whether the session is now authenticated.</param>
/// <param name="Cancelled">True when nothing failed — the curator backed out, or it timed out.</param>
/// <param name="Message">What to tell the curator when <paramref name="Succeeded"/> is false.</param>
public readonly record struct AuthOutcome(bool Succeeded, bool Cancelled, string Message)
{
    public static AuthOutcome Ok => new(true, false, string.Empty);

    public static AuthOutcome Abandoned => new(false, true, string.Empty);

    public static AuthOutcome Failed(string message) => new(false, false, message);
}

/// <summary>
/// The one auth session for the Revit process: browser sign-in, refresh, restore, sign-out.
/// </summary>
/// <remarks>
/// <para>
/// Owned by the add-in's <c>IExternalApplication</c> and reached through a static accessor. One per
/// process, not one per command: two commands each holding their own session would each hold their
/// own access token, and signing out in one would leave the other authenticated.
/// </para>
/// <para>
/// ⛔<c>HPS-15</c>: <see cref="AccessToken"/> lives in this object and nowhere else. It is never
/// written to disk and never logged. Only the refresh token reaches <see cref="ISecretStore"/>.
/// </para>
/// </remarks>
public sealed class AuthSession : IDisposable
{
    /// <summary>The single stored secret. One key, so sign-out has one thing to clear.</summary>
    private const string RefreshTokenKey = "refresh-token";

    private static readonly HttpClient Http = new();

    private readonly MantlePlaceEndpoints _endpoints;
    private readonly ISecretStore _secrets;
    private readonly object _gate = new();

    private CancellationTokenSource? _signIn;
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public AuthSession(MantlePlaceEndpoints endpoints, ISecretStore secrets)
    {
        _endpoints = endpoints;
        _secrets = secrets;
    }

    /// <summary>Raised whenever <see cref="State"/> changes, so the ribbon can follow.</summary>
    public event EventHandler<AuthState>? StateChanged;

    public AuthState State { get; private set; } = AuthStateMachine.Initial;

    public string UserEmail { get; private set; } = string.Empty;

    /// <summary>Whether a stored session would survive a restart (<c>HPS-16</c>).</summary>
    public bool IsPersistent => _secrets.IsPersistent;

    /// <summary>The bearer token, or empty. Memory-only (<c>HPS-15</c>).</summary>
    public string AccessToken
    {
        get
        {
            lock (_gate)
            {
                return _accessToken;
            }
        }
    }

    /// <summary>Whether the access token is within the skew of lapsing (<c>HPS-11</c>).</summary>
    public bool IsAccessTokenExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _accessToken.Length == 0 || TokenGrants.IsExpired(now, _expiresAt);
        }
    }

    /// <summary>
    /// Full browser sign-in: bind the loopback, open the browser, wait, exchange the code.
    /// </summary>
    public async Task<AuthOutcome> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!Raise(AuthEvent.BeginSignIn))
        {
            // A second sign-in while one is in flight is ignored, not queued (HPS-12).
            return AuthOutcome.Abandoned;
        }

        using CancellationTokenSource signIn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _signIn?.Dispose();
            _signIn = signIn;
        }

        // Bind BEFORE opening the browser (HPS-06).
        using LoopbackRedirectListener? listener =
            LoopbackRedirectListener.Start(_endpoints.LoopbackPorts, _endpoints.CallbackPath);

        if (listener is null)
        {
            Raise(AuthEvent.SignInFailed);
            return AuthOutcome.Failed(
                $"No free port in {_endpoints.LoopbackPorts[0]}–{_endpoints.LoopbackPorts[^1]} to receive the "
                + "sign-in response, so the browser was not opened. Close other Mantle Place sessions and try again.");
        }

        string verifier = PkceCodes.MakeCodeVerifier();
        string challenge = PkceCodes.MakeCodeChallengeS256(verifier);
        string state = PkceCodes.MakeState();

        string authorizeUrl = AuthUrls.BuildAuthorizeUrl(
            _endpoints.WebLoginUrl,
            listener.RedirectUri,
            challenge,
            state);

        if (!TryOpenBrowser(authorizeUrl, out string browserError))
        {
            Raise(AuthEvent.SignInFailed);
            return AuthOutcome.Failed(browserError);
        }

        LoopbackResult result = await listener
            .WaitForCallbackAsync(state, TimeSpan.FromSeconds(_endpoints.SignInTimeoutSeconds), signIn.Token)
            .ConfigureAwait(false);

        if (result.Outcome is null)
        {
            // Timeout or explicit cancel. Neither latches Failed (HPS-09, HPS-12).
            Raise(AuthEvent.Cancel);
            return AuthOutcome.Abandoned;
        }

        if (!result.HasCode)
        {
            Raise(AuthEvent.SignInFailed);
            return AuthOutcome.Failed(result.Message);
        }

        string? failure = await ExchangeCodeAsync(result.Callback!.Code, verifier, signIn.Token).ConfigureAwait(false);
        if (failure is not null)
        {
            Raise(AuthEvent.SignInFailed);
            return AuthOutcome.Failed(failure);
        }

        Raise(AuthEvent.SignInSucceeded);
        return AuthOutcome.Ok;
    }

    /// <summary>
    /// Aborts an in-flight sign-in. Closing a window is not this — only an explicit cancel is.
    /// </summary>
    public void CancelSignIn()
    {
        lock (_gate)
        {
            _signIn?.Cancel();
        }
    }

    /// <summary>
    /// Startup restore from the stored refresh token (<c>HPS-13</c>).
    /// </summary>
    /// <remarks>
    /// Silent about absence: no stored token is not an error and causes no state change. Forgiving
    /// about failure: the in-memory tokens are cleared but the PERSISTED refresh token is kept, so a
    /// network blip at Revit start does not force a re-login.
    /// </remarks>
    public async Task<AuthOutcome> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!_secrets.TryLoad(RefreshTokenKey, out string stored) || stored.Length == 0)
        {
            return AuthOutcome.Abandoned;
        }

        lock (_gate)
        {
            _refreshToken = stored;
        }

        if (!Raise(AuthEvent.BeginRestore))
        {
            return AuthOutcome.Abandoned;
        }

        string? failure = await RefreshGrantAsync(stored, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            lock (_gate)
            {
                _accessToken = string.Empty;
                _expiresAt = DateTimeOffset.MinValue;
            }

            Raise(AuthEvent.RefreshFailed);
            return AuthOutcome.Failed(failure);
        }

        Raise(AuthEvent.RefreshSucceeded);
        return AuthOutcome.Ok;
    }

    /// <summary>Mints a fresh access token from the refresh token.</summary>
    public async Task<AuthOutcome> RefreshAsync(CancellationToken cancellationToken = default)
    {
        string refreshToken;
        lock (_gate)
        {
            refreshToken = _refreshToken;
        }

        if (refreshToken.Length == 0)
        {
            return AuthOutcome.Abandoned;
        }

        if (!Raise(AuthEvent.BeginRefresh))
        {
            return AuthOutcome.Abandoned;
        }

        string? failure = await RefreshGrantAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        Raise(failure is null ? AuthEvent.RefreshSucceeded : AuthEvent.RefreshFailed);
        return failure is null ? AuthOutcome.Ok : AuthOutcome.Failed(failure);
    }

    /// <summary>Drops the session and clears the store (<c>HPS-17</c>).</summary>
    public void SignOut()
    {
        lock (_gate)
        {
            _signIn?.Cancel();
            _accessToken = string.Empty;
            _refreshToken = string.Empty;
            _expiresAt = DateTimeOffset.MinValue;
        }

        UserEmail = string.Empty;
        _secrets.Clear(RefreshTokenKey);
        Raise(AuthEvent.SignOut);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _signIn?.Dispose();
            _signIn = null;
        }
    }

    private async Task<string?> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["auth_code"] = code,
            ["code_verifier"] = verifier,
        });

        return await PostGrantAsync(_endpoints.TokenEndpointUrl, body, apiKey: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> RefreshGrantAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (_endpoints.RefreshTokenUrl is not { } url)
        {
            return "This plugin has no Supabase project configured, so it cannot renew a sign-in. "
                + $"Set supabaseUrl and supabaseAnonKey in {MantlePlaceEndpoints.ConfigPath}.";
        }

        string body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
        });

        return await PostGrantAsync(url, body, _endpoints.SupabaseAnonKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts a grant request and applies the response. <c>null</c> on success.
    /// </summary>
    private async Task<string?> PostGrantAsync(
        string url,
        string body,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        string responseBody;
        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach mantle.place to complete sign-in: {ex.Message}";
        }

        if (TokenGrants.TryParse(responseBody, out TokenGrant? grant) is { } failure)
        {
            return failure;
        }

        Apply(grant!);
        return null;
    }

    /// <summary>
    /// Takes a grant. Every SUCCESSFUL grant re-persists the refresh token, so the stored copy never
    /// goes stale (<c>HPS-13</c>).
    /// </summary>
    private void Apply(TokenGrant grant)
    {
        string toPersist;
        lock (_gate)
        {
            _accessToken = grant.AccessToken;

            // ⛔HPS-10: a response that omits refresh_token keeps the prior one. Wiping it here
            // costs the curator their session at the next restart, hours later, with no cause they
            // could connect it to.
            _refreshToken = TokenGrants.ChooseRefreshToken(grant.RefreshToken, _refreshToken);
            _expiresAt = TokenGrants.ExpiryFrom(DateTimeOffset.UtcNow, grant.ExpiresInSeconds);
            toPersist = _refreshToken;
        }

        if (grant.Email.Length > 0)
        {
            UserEmail = grant.Email;
        }

        if (toPersist.Length > 0)
        {
            _secrets.Save(RefreshTokenKey, toPersist);
        }
    }

    /// <summary>Applies an event. <c>false</c> when the machine declined to move.</summary>
    private bool Raise(AuthEvent signal)
    {
        AuthState next;
        lock (_gate)
        {
            next = AuthStateMachine.NextState(State, signal);
            if (next == State)
            {
                return false;
            }

            State = next;
        }

        StateChanged?.Invoke(this, next);
        return true;
    }

    /// <summary>
    /// ⛔<c>HPS-05</c>: the system browser, never an embedded webview and never a password field in
    /// Revit.
    /// </summary>
    private static bool TryOpenBrowser(string url, out string error)
    {
        error = string.Empty;
        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = "Could not open your browser to sign in. Open this address manually: " + url;
            return false;
        }
    }
}
