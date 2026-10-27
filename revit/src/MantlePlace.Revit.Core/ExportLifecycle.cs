using System.Text.Json;
using System.Text.RegularExpressions;

namespace MantlePlace.Revit.Core;

/// <summary>
/// One status this plugin asks the platform to move an export to
/// (<c>POST /api/v1/exports/{exportId}/advance</c>).
/// </summary>
/// <param name="Status">The status requested. Never <see cref="ExportStatus.Sent"/>: the web creates that.</param>
/// <param name="Progress">0 to 100, on an importing progress report; otherwise <c>null</c>.</param>
/// <param name="Reason">A short code, with <see cref="ExportStatus.Failed"/>.</param>
/// <param name="Message">The plugin's own words for the curator, with <see cref="ExportStatus.Failed"/>.</param>
public readonly record struct ExportTransition(ExportStatus Status, int? Progress, string? Reason, string? Message);

/// <summary>The transitions a plugin may send, built so that the common ones need no argument.</summary>
public static class ExportTransitions
{
    /// <summary>The contract caps <c>message</c> at 1000 characters.</summary>
    public const int MessageLimit = 1000;

    /// <summary>The reason a bundle this plugin cannot read is failed with.</summary>
    public const string UnsupportedVersionReason = "unsupported_version";

    /// <summary>The export is seen: <c>sent</c> to <c>received</c>.</summary>
    public static ExportTransition Received => new(ExportStatus.Received, null, null, null);

    /// <summary>The bundle is not ready to fetch yet.</summary>
    public static ExportTransition WaitingForBundle => new(ExportStatus.WaitingForBundle, null, null, null);

    /// <summary>The bundle is fetchable and the import dialog is open, waiting for the curator.</summary>
    public static ExportTransition AwaitingOptions => new(ExportStatus.AwaitingOptions, null, null, null);

    /// <summary>
    /// The import starts. The platform starts it at progress 0 and ignores a progress sent with
    /// the first call, so none is sent.
    /// </summary>
    public static ExportTransition StartImport => new(ExportStatus.Importing, null, null, null);

    /// <summary>A progress report while importing.</summary>
    public static ExportTransition ReportProgress(int percent) => new(ExportStatus.Importing, percent, null, null);

    /// <summary>The bundle is in the project.</summary>
    public static ExportTransition Imported => new(ExportStatus.Imported, null, null, null);

    /// <summary>The curator closed the import dialog.</summary>
    public static ExportTransition Cancelled => new(ExportStatus.Cancelled, null, null, null);

    /// <summary>
    /// A failure, with its code and the plugin's own words. A message past the contract's limit is
    /// cut to it, so a long exception text still reaches the curator.
    /// </summary>
    public static ExportTransition Failed(string reason, string? message)
        => new(
            ExportStatus.Failed,
            null,
            reason,
            message is { Length: > MessageLimit } ? message[..MessageLimit] : message);

    /// <summary>A bundle this plugin cannot read.</summary>
    public static ExportTransition UnsupportedVersion(string? message)
        => Failed(UnsupportedVersionReason, message);
}

/// <summary>
/// What the contract lets a plugin send, and in what order (the platform export contract, section 4).
/// </summary>
/// <remarks>
/// <para>
/// The server refuses the same moves with <c>409 invalid_transition</c> or <c>400 invalid_body</c>.
/// Refusing them here first means a bug in the plugin is a local message with the move named, not a
/// round trip that blames "the web moved it first". The one thing only the server can know is that
/// the export moved since this plugin last read it, and that is the 409 the client reports as such.
/// </para>
/// <para>
/// ⛔ <b>The two codes that belong to the web are refused locally</b>: <c>not_received</c> and
/// <c>host_disconnected</c>. The server does not enforce this, so this is the only enforcer.
/// </para>
/// </remarks>
public static partial class ExportLifecycle
{
    /// <summary>A reason is a code, never free text: it goes to analytics unchanged.</summary>
    public const string ReasonPattern = "^[a-z0-9_]{1,64}$";

    /// <summary>The codes the web sets and a plugin must not send.</summary>
    public static IReadOnlyList<string> WebOnlyReasons { get; } = ["not_received", "host_disconnected"];

    /// <summary>
    /// <c>null</c> when a plugin may send <paramref name="transition"/> to an export now in
    /// <paramref name="from"/>; otherwise why it may not.
    /// </summary>
    public static string? Check(ExportStatus from, ExportTransition transition)
    {
        if (ExportStatuses.IsTerminal(from))
        {
            return $"The export is already {ExportStatuses.ToWire(from)}; nothing moves it again.";
        }

        if (transition.Status == ExportStatus.Sent)
        {
            return "A plugin never sends 'sent'; the web creates an export as sent.";
        }

        if (transition.Progress is { } percent && percent is < 0 or > 100)
        {
            return $"Progress {percent} is outside 0 to 100.";
        }

        return transition.Status switch
        {
            ExportStatus.Received => From(from, transition, ExportStatus.Sent),
            ExportStatus.WaitingForBundle => From(from, transition, ExportStatus.Received),
            ExportStatus.AwaitingOptions => From(
                from, transition, ExportStatus.Received, ExportStatus.WaitingForBundle),
            ExportStatus.Importing => CheckImporting(from, transition),
            ExportStatus.Imported => From(from, transition, ExportStatus.Importing),
            ExportStatus.Cancelled => From(from, transition, ExportStatus.AwaitingOptions),
            ExportStatus.Failed => CheckFailed(transition),
            _ => $"Status {transition.Status} is not one a plugin sends.",
        };
    }

    /// <summary>
    /// The request body for <paramref name="transition"/>: exactly the keys the route accepts,
    /// since it refuses an unknown one.
    /// </summary>
    public static string BuildBody(string connectionId, ExportTransition transition)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("connectionId", connectionId);
            writer.WriteString("status", ExportStatuses.ToWire(transition.Status));
            if (transition.Status == ExportStatus.Importing && transition.Progress is { } percent)
            {
                writer.WriteNumber("progress", percent);
            }

            if (transition.Status == ExportStatus.Failed)
            {
                if (transition.Reason is not null)
                {
                    writer.WriteString("reason", transition.Reason);
                }

                if (!string.IsNullOrEmpty(transition.Message))
                {
                    writer.WriteString("message", transition.Message);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? From(ExportStatus from, ExportTransition transition, params ExportStatus[] allowed)
        => Array.IndexOf(allowed, from) >= 0
            ? null
            : $"'{ExportStatuses.ToWire(transition.Status)}' cannot follow '{ExportStatuses.ToWire(from)}'; "
                + $"it follows {string.Join(" or ", allowed.Select(s => $"'{ExportStatuses.ToWire(s)}'"))}.";

    private static string? CheckImporting(ExportStatus from, ExportTransition transition)
    {
        if (from == ExportStatus.AwaitingOptions)
        {
            return null;
        }

        if (from != ExportStatus.Importing)
        {
            return From(from, transition, ExportStatus.AwaitingOptions, ExportStatus.Importing);
        }

        return transition.Progress is null
            ? "An importing report needs a progress from 0 to 100."
            : null;
    }

    private static string? CheckFailed(ExportTransition transition)
    {
        if (string.IsNullOrEmpty(transition.Reason))
        {
            return "A failure needs a reason code.";
        }

        if (!ReasonRegex().IsMatch(transition.Reason))
        {
            return $"The reason '{transition.Reason}' is not a code ({ReasonPattern}).";
        }

        if (WebOnlyReasons.Contains(transition.Reason))
        {
            return $"The reason '{transition.Reason}' belongs to the web; a plugin must not send it.";
        }

        return transition.Message is { Length: > ExportTransitions.MessageLimit }
            ? $"The message is longer than {ExportTransitions.MessageLimit} characters."
            : null;
    }

    // `$` in .NET also matches before a trailing newline, which the contract's pattern (a JavaScript
    // one) does not, so the end is pinned with `\z`.
    [GeneratedRegex(@"^[a-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonRegex();
}

/// <summary>
/// Paces the progress reports of one import: about one every 2 seconds, starting at 0.
/// </summary>
/// <remarks>
/// <para>
/// The route is rate limited per source address, and a plugin that reports on every step would
/// spend the allowance on one import. The clock is injected, so the pace is asserted without
/// waiting.
/// </para>
/// <para>
/// Entering <c>importing</c> is the report at 0 (the platform starts the import there), so
/// <see cref="Start"/> returns 0 and stamps the time; the first progress report follows no sooner
/// than <see cref="Interval"/> after it. A report never goes down, and one that would repeat the
/// last is not sent.
/// </para>
/// </remarks>
public sealed class ImportProgressPacer
{
    /// <summary>The spacing the contract asks for.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _lastSent;
    private int _lastPercent = -1;

    public ImportProgressPacer(Func<DateTimeOffset> now)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    /// <summary>The import started: the report at 0.</summary>
    public int Start()
    {
        _lastSent = _now();
        _lastPercent = 0;
        return 0;
    }

    /// <summary>
    /// Whether to send a report now, and which. <c>false</c> inside the interval, before
    /// <see cref="Start"/>, or when the percent has not moved.
    /// </summary>
    public bool TryNext(int percent, out int toSend)
    {
        toSend = _lastPercent;
        if (_lastPercent < 0)
        {
            return false;
        }

        int clamped = Math.Clamp(percent, 0, 100);
        DateTimeOffset now = _now();
        if (now - _lastSent < Interval || clamped <= _lastPercent)
        {
            return false;
        }

        _lastSent = now;
        _lastPercent = clamped;
        toSend = clamped;
        return true;
    }
}
