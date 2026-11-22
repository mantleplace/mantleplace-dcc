using System.Text.Json;
using System.Text.RegularExpressions;

namespace MantlePlace.Core;

public readonly record struct ExportTransition(ExportStatus Status, int? Progress, string? Reason, string? Message);

public static class ExportTransitions
{
    public const int MessageLimit = 1000;

    public const string UnsupportedVersionReason = "unsupported_version";

    public static ExportTransition Received => new(ExportStatus.Received, null, null, null);

    public static ExportTransition WaitingForBundle => new(ExportStatus.WaitingForBundle, null, null, null);

    public static ExportTransition AwaitingOptions => new(ExportStatus.AwaitingOptions, null, null, null);

    public static ExportTransition StartImport => new(ExportStatus.Importing, null, null, null);

    public static ExportTransition ReportProgress(int percent) => new(ExportStatus.Importing, percent, null, null);

    public static ExportTransition Imported => new(ExportStatus.Imported, null, null, null);

    public static ExportTransition Cancelled => new(ExportStatus.Cancelled, null, null, null);

    public static ExportTransition Failed(string reason, string? message)
        => new(
            ExportStatus.Failed,
            null,
            reason,
            message is { Length: > MessageLimit } ? message[..MessageLimit] : message);

    public static ExportTransition UnsupportedVersion(string? message)
        => Failed(UnsupportedVersionReason, message);
}

public static partial class ExportLifecycle
{
    public const string ReasonPattern = "^[a-z0-9_]{1,64}$";

    public static IReadOnlyList<string> WebOnlyReasons { get; } = ["not_received", "host_disconnected"];

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

    [GeneratedRegex(@"^[a-z0-9_]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonRegex();
}

public sealed class ImportProgressPacer
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _lastSent;
    private int _lastPercent = -1;

    public ImportProgressPacer(Func<DateTimeOffset> now)
    {
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    public int Start()
    {
        _lastSent = _now();
        _lastPercent = 0;
        return 0;
    }

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
