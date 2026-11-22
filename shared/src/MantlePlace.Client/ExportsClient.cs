using System.Net.Http;
using MantlePlace.PlatformApi;
using MantlePlace.Core;

namespace MantlePlace.Client;

public interface IBearerSession
{
    string AccessToken { get; }

    bool CanRenewSession { get; }

    Task<bool> RefreshAsync(CancellationToken cancellationToken);
}

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

public enum ExportCallOutcome
{
    Ok,

    RefusedLocally,

    WebMovedFirst,

    RateLimited,

    RetryLater,

    Rejected,

    SignInNeeded,
}

public readonly record struct ExportCall<T>(
    ExportCallOutcome Outcome,
    T? Value,
    string Message,
    int Status,
    string Code,
    int RetryAfterMs)
{
    public bool Succeeded => Outcome == ExportCallOutcome.Ok;

    public bool IsFault => Outcome is not (ExportCallOutcome.Ok or ExportCallOutcome.WebMovedFirst);

    internal static ExportCall<T> Ok(T value, int status)
        => new(ExportCallOutcome.Ok, value, string.Empty, status, string.Empty, 0);

    internal static ExportCall<T> Of(ExportCallOutcome outcome, string message, int status = 0, string code = "", int retryAfterMs = 0)
        => new(outcome, default, message, status, code, retryAfterMs);
}

public sealed class ExportsClient : IExportRoutes
{
    public const int MaxRateLimitRetries = 2;

    public const int MaxRetryAfterMs = 30_000;

    private readonly MantlePlaceEndpoints _endpoints;
    private readonly IBearerSession _session;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

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
