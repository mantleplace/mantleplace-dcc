using System.Net.Http;
using MantlePlace.PlatformApi;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>The bearer session the export routes are called on: a token, and a way to renew it.</summary>
/// <remarks>
/// The seam exists so the 401 handling is asserted without a platform (<c>HPS-42</c>). The real one is
/// <see cref="AuthBearerSession"/>.
/// </remarks>
public interface IBearerSession
{
    /// <summary>The access token, or empty when there is none.</summary>
    string AccessToken { get; }

    /// <summary>Whether a renewal is possible: a refresh token is held.</summary>
    bool CanRenewSession { get; }

    /// <summary>Renews the access token. <c>true</c> when it now holds a fresh one.</summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken);
}

/// <summary>The sign-in this process holds, as the bearer session the export routes use.</summary>
public sealed class AuthBearerSession : IBearerSession
{
    private readonly AuthSession _session;

    public AuthBearerSession(AuthSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public string AccessToken => _session.AccessToken;

    public bool CanRenewSession => _session.CanRenewSession;

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
        => (await _session.RefreshAsync(cancellationToken).ConfigureAwait(false)).Succeeded;
}

/// <summary>How an export call ended.</summary>
public enum ExportCallOutcome
{
    /// <summary>The platform answered 2xx and the body read.</summary>
    Ok,

    /// <summary>The transition was refused here, before any request: a plugin bug, not the platform.</summary>
    RefusedLocally,

    /// <summary>The web moved the export first. Not a fault.</summary>
    WebMovedFirst,

    /// <summary>Still over the rate limit after waiting as told.</summary>
    RateLimited,

    /// <summary>A 5xx, or no answer: try again later.</summary>
    RetryLater,

    /// <summary>Another 4xx: the same request will not succeed.</summary>
    Rejected,

    /// <summary>A 401 that a refresh did not cure, or no session to refresh: sign in again.</summary>
    SignInNeeded,
}

/// <summary>The result of one export call: the value on success, or what happened.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Value">The parsed body, for <see cref="ExportCallOutcome.Ok"/>.</param>
/// <param name="Message">A sentence for the log or the curator; empty on success.</param>
/// <param name="Status">The HTTP status of the last answer, or 0 when there was none.</param>
/// <param name="Code">The refusal code of the last answer, or empty.</param>
/// <param name="RetryAfterMs">With <see cref="ExportCallOutcome.RateLimited"/>, the wait the platform last asked for.</param>
public readonly record struct ExportCall<T>(
    ExportCallOutcome Outcome,
    T? Value,
    string Message,
    int Status,
    string Code,
    int RetryAfterMs)
{
    public bool Succeeded => Outcome == ExportCallOutcome.Ok;

    /// <summary>Everything but success and the web having moved the export first.</summary>
    public bool IsFault => Outcome is not (ExportCallOutcome.Ok or ExportCallOutcome.WebMovedFirst);

    internal static ExportCall<T> Ok(T value, int status)
        => new(ExportCallOutcome.Ok, value, string.Empty, status, string.Empty, 0);

    internal static ExportCall<T> Of(ExportCallOutcome outcome, string message, int status = 0, string code = "", int retryAfterMs = 0)
        => new(outcome, default, message, status, code, retryAfterMs);
}

/// <summary>
/// The two v1 export routes, on the bearer session: list the exports for a connection, and advance
/// one (the platform export contract, section 5).
/// </summary>
/// <remarks>
/// <para>
/// Every decision is made in <c>MantlePlace.Revit.Core</c> (<see cref="ExportRefusals"/>,
/// <see cref="ExportLifecycle"/>, <see cref="HostExports"/>); what is here is the request, the single
/// refresh on a 401, and the wait on a 429.
/// </para>
/// <para>
/// <b>401</b>: the session renews once and the request is sent again; a second 401, a failed renewal,
/// or no renewal to try is <see cref="ExportCallOutcome.SignInNeeded"/>. <b>409
/// invalid_transition</b> is <see cref="ExportCallOutcome.WebMovedFirst"/>, not a fault. <b>429</b>
/// waits <c>retryAfterMs</c> (capped, so a hostile value cannot park the plugin) and retries, at
/// most <see cref="MaxRateLimitRetries"/> times. Any other code is handled by its status class, and a
/// 5xx is reported rather than retried here: the caller owns when to ask again, because it knows
/// whether anyone is still waiting.
/// </para>
/// </remarks>
public sealed class ExportsClient : IExportRoutes
{
    /// <summary>How many times a 429 is waited out before it is reported.</summary>
    public const int MaxRateLimitRetries = 2;

    /// <summary>The longest single wait honoured, whatever the platform asks.</summary>
    public const int MaxRetryAfterMs = 30_000;

    private readonly MantlePlaceEndpoints _endpoints;
    private readonly IBearerSession _session;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="endpoints">Where the routes are.</param>
    /// <param name="session">The sign-in.</param>
    /// <param name="handler">A message handler, for tests; the shared client when <c>null</c>.</param>
    /// <param name="delay">How a 429 is waited out, for tests; a real delay when <c>null</c>.</param>
    public ExportsClient(
        MantlePlaceEndpoints endpoints,
        IBearerSession session,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _handler = handler;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The exports that target this connection and are not terminal, oldest first.</summary>
    public async Task<ExportCall<IReadOnlyList<HostExport>>> ListAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        (ExportCall<string> call, string body) = await SendAsync(
            (api, ct) => api.ListPendingExportsAsync(connectionId, ct), null, cancellationToken)
            .ConfigureAwait(false);
        if (!call.Succeeded)
        {
            return ExportCall<IReadOnlyList<HostExport>>.Of(
                call.Outcome, call.Message, call.Status, call.Code, call.RetryAfterMs);
        }

        return HostExports.TryParseList(body, out IReadOnlyList<HostExport> exports) is { } parseError
            ? ExportCall<IReadOnlyList<HostExport>>.Of(ExportCallOutcome.RetryLater, parseError, call.Status)
            : ExportCall<IReadOnlyList<HostExport>>.Ok(exports, call.Status);
    }

    /// <summary>
    /// Moves one export forward. The transition is checked against the export's status first, and a
    /// move the contract refuses is not sent.
    /// </summary>
    /// <param name="current">The export as this plugin last read it.</param>
    /// <param name="connectionId">This plugin's connection id.</param>
    /// <param name="transition">What to move it to.</param>
    public async Task<ExportCall<HostExport>> AdvanceAsync(
        HostExport current,
        string connectionId,
        ExportTransition transition,
        CancellationToken cancellationToken)
    {
        if (ExportLifecycle.Check(current.Status, transition) is { } refused)
        {
            return ExportCall<HostExport>.Of(ExportCallOutcome.RefusedLocally, refused);
        }

        (ExportCall<string> call, string body) = await SendAsync(
            (api, ct) => api.AdvanceExportAsync(current.Id, new Body6(), ct),
            ExportLifecycle.BuildBody(connectionId, transition),
            cancellationToken)
            .ConfigureAwait(false);
        if (!call.Succeeded)
        {
            return ExportCall<HostExport>.Of(
                call.Outcome, call.Message, call.Status, call.Code, call.RetryAfterMs);
        }

        return HostExports.TryParse(body, out HostExport updated) is { } parseError
            ? ExportCall<HostExport>.Of(ExportCallOutcome.RetryLater, parseError, call.Status)
            : ExportCall<HostExport>.Ok(updated, call.Status);
    }

    private string Base() => _endpoints.ApiBaseUrl.TrimEnd('/');

    /// <summary>One Platform API call through the generated client, with the renewal and 429 rules above.</summary>
    private async Task<(ExportCall<string> Call, string Body)> SendAsync(
        Func<PlatformApiClient, CancellationToken, Task> operation,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        bool refreshed = false;
        int waits = 0;

        while (true)
        {
            string accessToken = _session.AccessToken;
            if (accessToken.Length == 0)
            {
                if (!refreshed && _session.CanRenewSession)
                {
                    refreshed = true;
                    if (await _session.RefreshAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                }

                return (ExportCall<string>.Of(ExportCallOutcome.SignInNeeded, "Sign in to Mantle Place first."), string.Empty);
            }

            int status;
            string body;
            try
            {
                (status, body) = await PlatformApiCalls.For(Base(), _handler)
                    .SendAsync(operation, accessToken, cancellationToken, jsonBody)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return (ExportCall<string>.Of(ExportCallOutcome.RetryLater, $"Could not reach mantle.place: {ex.Message}"), string.Empty);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (ExportCall<string>.Of(ExportCallOutcome.RetryLater, "The request to mantle.place timed out."), string.Empty);
            }

            if (status is >= 200 and < 300)
            {
                return (ExportCall<string>.Ok(body, status), body);
            }

            ExportRefusal refusal = ExportRefusals.Classify(status, body);
            switch (refusal.Kind)
            {
                case ExportRefusalKind.SignInNeeded:
                    // Once only, and only when a renewal is possible. Asking the platform beats
                    // guessing from the clock, and a second 401 means the renewal did not take.
                    if (!refreshed && _session.CanRenewSession)
                    {
                        refreshed = true;
                        if (await _session.RefreshAsync(cancellationToken).ConfigureAwait(false))
                        {
                            continue;
                        }
                    }

                    break;

                case ExportRefusalKind.RateLimited when waits < MaxRateLimitRetries:
                    waits++;
                    await _delay(
                        TimeSpan.FromMilliseconds(Math.Min(refusal.RetryAfterMs, MaxRetryAfterMs)),
                        cancellationToken).ConfigureAwait(false);
                    continue;
            }

            return (ExportCall<string>.Of(
                    OutcomeOf(refusal.Kind), refusal.Message, refusal.Status, refusal.Code, refusal.RetryAfterMs),
                body);
        }
    }

    private static ExportCallOutcome OutcomeOf(ExportRefusalKind kind) => kind switch
    {
        ExportRefusalKind.SignInNeeded => ExportCallOutcome.SignInNeeded,
        ExportRefusalKind.WebMovedFirst => ExportCallOutcome.WebMovedFirst,
        ExportRefusalKind.RateLimited => ExportCallOutcome.RateLimited,
        ExportRefusalKind.RetryLater => ExportCallOutcome.RetryLater,
        _ => ExportCallOutcome.Rejected,
    };
}
