using System.Net.Http;
using System.Text.Json;
using MantlePlace.PlatformApi;
using MantlePlace.Core;

namespace MantlePlace.Client;

public readonly record struct AuthOutcome(bool Succeeded, bool Cancelled, string Message)
{
    public static AuthOutcome Ok => new(true, false, string.Empty);

    public static AuthOutcome Abandoned => new(false, true, string.Empty);

    public static AuthOutcome Failed(string message) => new(false, false, message);
}

internal readonly record struct GrantFailure(string Message, bool IsDefinitive);

public sealed class AuthSession : IDisposable
{
    private const string RefreshTokenKey = "refresh-token";

    private readonly HttpMessageHandler? _handler;

    private readonly MantlePlaceEndpoints _endpoints;
    private readonly ISecretStore _secrets;
    private readonly object _gate = new();

    private CancellationTokenSource? _signIn;
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public AuthSession(MantlePlaceEndpoints endpoints, ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        _endpoints = endpoints;
        _secrets = secrets;
        _handler = handler;
    }

    public event EventHandler<AuthState>? StateChanged;

    public AuthState State { get; private set; } = AuthStateMachine.Initial;

    public string UserEmail { get; private set; } = string.Empty;

    public bool IsPersistent => _secrets.IsPersistent;

    public bool CanRenewSession
    {
        get
        {
            lock (_gate)
            {
                return _refreshToken.Length > 0;
            }
        }
    }

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

    public DateTimeOffset AccessTokenExpiresAt
    {
        get
        {
            lock (_gate)
            {
                return _expiresAt;
            }
        }
    }

    public bool IsAccessTokenExpired(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _accessToken.Length == 0 || TokenGrants.IsExpired(now, _expiresAt);
        }
    }

    public async Task<AuthOutcome> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!Raise(AuthEvent.BeginSignIn))
        {
            return AuthOutcome.Abandoned;
        }

        CancellationTokenSource signIn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _signIn?.Dispose();
            _signIn = signIn;
        }

        try
        {
            using LoopbackRedirectListener? listener = _endpoints.LoopbackPorts.Count > 0
                ? LoopbackRedirectListener.Start(_endpoints.LoopbackPorts, _endpoints.CallbackPath)
                : LoopbackRedirectListener.StartEphemeral(_endpoints.CallbackPath);

            if (listener is null)
            {
                Raise(AuthEvent.SignInFailed);
                return AuthOutcome.Failed(DescribeBindFailure(_endpoints.LoopbackPorts));
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
                Raise(AuthEvent.Cancel);
                return AuthOutcome.Abandoned;
            }

            if (!result.HasCode)
            {
                Raise(AuthEvent.SignInFailed);
                return AuthOutcome.Failed(result.Message);
            }

            GrantFailure? failure = await ExchangeCodeAsync(result.Callback!.Code, verifier, signIn.Token).ConfigureAwait(false);
            if (failure is { } exchangeFailure)
            {
                Raise(AuthEvent.SignInFailed);
                return AuthOutcome.Failed(exchangeFailure.Message);
            }

            Raise(AuthEvent.SignInSucceeded);
            return AuthOutcome.Ok;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_signIn, signIn))
                {
                    _signIn = null;
                }
            }

            signIn.Dispose();
        }
    }

    public void CancelSignIn()
    {
        lock (_gate)
        {
            CancelInFlight();
        }
    }

    private void CancelInFlight()
    {
        try
        {
            _signIn?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

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

        GrantFailure? failure = await RefreshGrantAsync(stored, cancellationToken).ConfigureAwait(false);
        if (failure is { } restoreFailure)
        {
            return FailRenewal(restoreFailure, stored);
        }

        Raise(AuthEvent.RefreshSucceeded);
        return AuthOutcome.Ok;
    }

    public async Task<AuthOutcome> RefreshAsync(CancellationToken cancellationToken = default)
    {
        string refreshToken = ReadFreshestRefreshToken();

        if (refreshToken.Length == 0)
        {
            return AuthOutcome.Abandoned;
        }

        if (!Raise(AuthEvent.BeginRefresh))
        {
            return AuthOutcome.Abandoned;
        }

        lock (_gate)
        {
            _refreshToken = refreshToken;
        }

        GrantFailure? failure = await RefreshGrantAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        if (failure is { } refreshFailure)
        {
            return FailRenewal(refreshFailure, refreshToken);
        }

        Raise(AuthEvent.RefreshSucceeded);
        return AuthOutcome.Ok;
    }

    private string ReadFreshestRefreshToken()
    {
        string inMemory;
        lock (_gate)
        {
            inMemory = _refreshToken;
        }

        return _secrets.TryLoad(RefreshTokenKey, out string stored) && stored.Length > 0
            ? stored
            : inMemory;
    }

    private AuthOutcome FailRenewal(GrantFailure failure, string presented)
    {
        lock (_gate)
        {
            _accessToken = string.Empty;
            _expiresAt = DateTimeOffset.MinValue;
        }

        if (!failure.IsDefinitive)
        {
            Raise(AuthEvent.RefreshFailed);
            return AuthOutcome.Failed(failure.Message);
        }

        string freshest = ReadFreshestRefreshToken();
        if (freshest.Length > 0 && !string.Equals(freshest, presented, StringComparison.Ordinal))
        {
            Raise(AuthEvent.SignOut);
            lock (_gate)
            {
                _refreshToken = freshest;
            }

            return AuthOutcome.Abandoned;
        }

        lock (_gate)
        {
            _refreshToken = string.Empty;
        }

        UserEmail = string.Empty;
        _secrets.Clear(RefreshTokenKey);
        Raise(AuthEvent.SignOut);

        return AuthOutcome.Failed($"{failure.Message} Sign in again to continue.");
    }

    public void SignOut()
    {
        lock (_gate)
        {
            CancelInFlight();
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

    private async Task<GrantFailure?> ExchangeCodeAsync(string code, string verifier, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["auth_code"] = code,
            ["code_verifier"] = verifier,
        });

        return await PostGrantAsync(
                PlatformApiCalls.OriginOf(_endpoints.TokenEndpointUrl),
                (api, ct) => api.ExchangeNativeAuthCodeAsync(new Body(), ct),
                body,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GrantFailure?> RefreshGrantAsync(string refreshToken, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
        });

        return await PostGrantAsync(
                PlatformApiCalls.OriginOf(_endpoints.RefreshEndpointUrl),
                (api, ct) => api.RefreshNativeSessionAsync(new Body2(), ct),
                body,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GrantFailure?> PostGrantAsync(
        string baseUrl,
        Func<PlatformApiClient, CancellationToken, Task> operation,
        string body,
        CancellationToken cancellationToken)
    {
        string responseBody;
        int status;
        try
        {
            (status, responseBody) = await PlatformApiCalls.For(baseUrl, _handler)
                .SendAsync(operation, null, cancellationToken, body)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new GrantFailure($"Could not reach mantle.place: {ex.Message}", IsDefinitive: false);
        }

        if (ClientUpdatePrompt.MinimumVersionOf(status, responseBody) is { } minimumVersion)
        {
            return new GrantFailure(ClientUpdatePrompt.Sentence(minimumVersion), IsDefinitive: false);
        }

        if (TokenGrants.TryParse(responseBody, out TokenGrant? grant) is { } failure)
        {
            return new GrantFailure(failure, TokenGrants.IsDefinitiveRejection(status, responseBody));
        }

        Apply(grant!);
        return null;
    }

    private void Apply(TokenGrant grant)
    {
        string toPersist;
        lock (_gate)
        {
            _accessToken = grant.AccessToken;

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

    private static string DescribeBindFailure(IReadOnlyList<int> configuredPorts)
    {
        if (configuredPorts.Count > 0)
        {
            string tried = string.Join(", ", configuredPorts);
            return $"None of the loopback ports this machine is configured to use ({tried}) could be "
                + "opened, so the browser was not opened. Remove the loopbackPorts override from "
                + $"{MantlePlaceEndpoints.ConfigPath} to let the operating system choose a port instead.";
        }

        return "Could not open a loopback port to receive the sign-in response, so the browser was "
            + "not opened. On Windows this is usually a reserved port range or a policy restriction "
            + "on local HTTP listeners rather than a port genuinely in use — the operating system "
            + "reports both the same way. To see the reserved ranges, run:\r\n\r\n"
            + "    netsh interface ipv4 show excludedportrange protocol=tcp";
    }

    private static bool TryOpenBrowser(string url, out string error)
    {
        if (ShellLauncher.TryOpen(url, out string refused))
        {
            error = string.Empty;
            return true;
        }

        error = "Could not open your browser to sign in. Open this address manually: " + refused;
        return false;
    }
}
