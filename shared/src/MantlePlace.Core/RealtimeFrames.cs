using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MantlePlace.Core;

public readonly record struct RealtimeBinding(string Table, string Filter)
{
    public const string Schema = "public";

    public const string AnyEvent = "*";

    public const string ExportsTable = "bundle_exports";

    public const string OrdersTable = "orders";

    public static RealtimeBinding Exports(string connectionId)
        => new(ExportsTable, "target_connection_id=eq." + connectionId);

    public static RealtimeBinding Order(string orderId)
        => new(OrdersTable, "id=eq." + orderId);
}

public static class RealtimeChannel
{
    public const string ProtocolVersion = "1.0.0";

    public const string SocketTopic = "phoenix";

    public const string WirePrefix = "realtime:";

    private static readonly int[] BackoffMs = [1_000, 2_000, 5_000, 10_000];

    public static string Topic(string userId) => "vault:" + userId;

    public static string WireTopic(string userId) => WirePrefix + Topic(userId);

    public static string? SocketUrl(string? location, string? publishableKey)
    {
        string key = (publishableKey ?? string.Empty).Trim();
        string at = (location ?? string.Empty).Trim().TrimEnd('/');
        if (key.Length == 0 || at.Length == 0)
        {
            return null;
        }

        if (at.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            at = "wss://" + at["https://".Length..];
        }
        else if (at.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            at = "ws://" + at["http://".Length..];
        }

        if (!Uri.TryCreate(at, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("ws" or "wss"))
        {
            return null;
        }

        if (!at.EndsWith("/websocket", StringComparison.OrdinalIgnoreCase))
        {
            at += "/websocket";
        }

        return at + (at.Contains('?', StringComparison.Ordinal) ? "&" : "?")
            + "apikey=" + Uri.EscapeDataString(key) + "&vsn=" + ProtocolVersion;
    }

    public static string? WhyOff(string? location, string? publishableKey)
    {
        bool noLocation = SocketUrl(location, "k") is null;
        bool noKey = string.IsNullOrWhiteSpace(publishableKey);
        if (!noLocation && !noKey)
        {
            return null;
        }

        string missing = (noLocation, noKey) switch
        {
            (true, true) => "no Realtime location (realtimeUrl) and no publishable key (realtimePublishableKey) are configured",
            (true, false) => "no usable Realtime location (realtimeUrl) is configured",
            _ => "no Realtime publishable key (realtimePublishableKey) is configured",
        };
        return $"Connected host is off: {missing}, so this Revit joins no channel and tracks no presence. Send to it is unavailable; Download still works.";
    }

    public static string? UserIdOf(string? accessToken)
    {
        string[] parts = (accessToken ?? string.Empty).Split('.');
        if (parts.Length != 3 || parts[1].Length == 0)
        {
            return null;
        }

        string base64 = parts[1].Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        try
        {
            using JsonDocument claims = JsonDocument.Parse(Convert.FromBase64String(base64));
            string subject = claims.RootElement.Str("sub");
            return subject.Length == 0 ? null : subject;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public static bool IsRefusal(string? reason)
        => (reason ?? string.Empty).Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);

    public static TimeSpan Backoff(int attempt)
        => TimeSpan.FromMilliseconds(BackoffMs[Math.Clamp(attempt, 1, BackoffMs.Length) - 1]);
}

public enum RealtimeMessageKind
{
    Reply,

    RowChange,

    ChannelError,

    Close,

    Ignored,

    Unreadable,
}

public sealed record RealtimeRowChange(string Table, string Type, string RecordJson, string OldRecordJson)
{
    public bool IsExport => string.Equals(Table, RealtimeBinding.ExportsTable, StringComparison.Ordinal);

    public bool IsOrder => string.Equals(Table, RealtimeBinding.OrdersTable, StringComparison.Ordinal);
}

public sealed record RealtimeMessage(
    RealtimeMessageKind Kind,
    string Topic,
    string Event,
    string? Ref,
    string? JoinRef,
    bool Ok,
    string Reason,
    RealtimeRowChange? Change,
    string PayloadJson);

public static class RealtimeFrames
{
    public static string Join(
        string wireTopic,
        string joinRef,
        string accessToken,
        string presenceKey,
        IEnumerable<RealtimeBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return Frame(wireTopic, "phx_join", joinRef, joinRef, writer =>
        {
            writer.WriteStartObject("config");
            writer.WriteStartObject("broadcast");
            writer.WriteBoolean("ack", false);
            writer.WriteBoolean("self", false);
            writer.WriteEndObject();
            writer.WriteStartObject("presence");
            writer.WriteString("key", presenceKey);
            writer.WriteBoolean("enabled", true);
            writer.WriteEndObject();
            writer.WriteStartArray("postgres_changes");
            foreach (RealtimeBinding binding in bindings)
            {
                writer.WriteStartObject();
                writer.WriteString("event", RealtimeBinding.AnyEvent);
                writer.WriteString("schema", RealtimeBinding.Schema);
                writer.WriteString("table", binding.Table);
                writer.WriteString("filter", binding.Filter);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("private", true);
            writer.WriteEndObject();
            writer.WriteString("access_token", accessToken);
        });
    }

    public static string Track(string wireTopic, string joinRef, string reference, string presenceJson)
        => Frame(wireTopic, "presence", reference, joinRef, writer =>
        {
            writer.WriteString("type", "presence");
            writer.WriteString("event", "track");
            writer.WritePropertyName("payload");
            writer.WriteRawValue(presenceJson);
        });

    public static string Untrack(string wireTopic, string joinRef, string reference)
        => Frame(wireTopic, "presence", reference, joinRef, writer =>
        {
            writer.WriteString("type", "presence");
            writer.WriteString("event", "untrack");
        });

    public static string Leave(string wireTopic, string joinRef, string reference)
        => Frame(wireTopic, "phx_leave", reference, joinRef, _ => { });

    public static string AccessToken(string wireTopic, string joinRef, string reference, string accessToken)
        => Frame(wireTopic, "access_token", reference, joinRef, writer => writer.WriteString("access_token", accessToken));

    public static string Heartbeat(string reference)
        => Frame(RealtimeChannel.SocketTopic, "heartbeat", reference, null, _ => { });

    public static RealtimeMessage Decode(string frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame ?? string.Empty);
        }
        catch (JsonException)
        {
            return Unreadable(string.Empty, string.Empty);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unreadable(string.Empty, string.Empty);
            }

            string topic = root.Str("topic");
            string name = root.Str("event");
            string? reference = RefOf(root, "ref");
            string? joinRef = RefOf(root, "join_ref");
            JsonElement payload = root.TryGetProperty("payload", out JsonElement p) ? p : default;
            string payloadJson = payload.ValueKind == JsonValueKind.Undefined ? "{}" : payload.GetRawText();

            RealtimeMessage Of(RealtimeMessageKind kind, bool ok = false, string reason = "", RealtimeRowChange? change = null)
                => new(kind, topic, name, reference, joinRef, ok, reason, change, payloadJson);

            switch (name)
            {
                case "phx_reply":
                {
                    bool ok = string.Equals(payload.Str("status"), "ok", StringComparison.Ordinal);
                    return Of(RealtimeMessageKind.Reply, ok, ok ? string.Empty : ReasonOf(payload));
                }

                case "phx_error":
                    return Of(RealtimeMessageKind.ChannelError, reason: "The channel failed (phx_error).");

                case "phx_close":
                    return Of(RealtimeMessageKind.Close);

                case "system":
                    return string.Equals(payload.Str("status"), "error", StringComparison.Ordinal)
                        ? Of(RealtimeMessageKind.ChannelError, reason: payload.Str("message") is { Length: > 0 } message ? message : "The channel failed.")
                        : Of(RealtimeMessageKind.Ignored);

                case "postgres_changes":
                {
                    JsonElement? data = payload.ValueKind == JsonValueKind.Object ? payload.Object("data") : null;
                    JsonElement? record = data?.Object("record");
                    if (data is not { } d || record is not { } r)
                    {
                        return Unreadable(topic, name);
                    }

                    JsonElement? old = d.Object("old_record");
                    return Of(
                        RealtimeMessageKind.RowChange,
                        change: new RealtimeRowChange(d.Str("table"), d.Str("type"), r.GetRawText(), old?.GetRawText() ?? "{}"));
                }

                default:
                    return Of(name.Length == 0 ? RealtimeMessageKind.Unreadable : RealtimeMessageKind.Ignored);
            }
        }
    }

    private static RealtimeMessage Unreadable(string topic, string name)
        => new(RealtimeMessageKind.Unreadable, topic, name, null, null, false, string.Empty, null, "{}");

    private static string? RefOf(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static string ReasonOf(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("response", out JsonElement response))
        {
            return "The platform refused the request.";
        }

        if (response.ValueKind == JsonValueKind.String && response.GetString() is { Length: > 0 } bare)
        {
            return bare;
        }

        string reason = response.Str("reason");
        if (reason.Length == 0)
        {
            reason = response.Str("message");
        }

        return reason.Length > 0 ? reason : "The platform refused the request.";
    }

    private static string Frame(string topic, string name, string reference, string? joinRef, Action<Utf8JsonWriter> payload)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("topic", topic);
            writer.WriteString("event", name);
            writer.WriteStartObject("payload");
            payload(writer);
            writer.WriteEndObject();
            writer.WriteString("ref", reference);
            if (joinRef is not null)
            {
                writer.WriteString("join_ref", joinRef);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string RefText(long counter) => counter.ToString(CultureInfo.InvariantCulture);
}
