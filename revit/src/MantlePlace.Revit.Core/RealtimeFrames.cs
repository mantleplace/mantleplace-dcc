using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// One <c>postgres_changes</c> subscription on the channel: every change to the rows of one table that
/// pass one filter (the platform export contract, section 3).
/// </summary>
/// <param name="Table">The table in the <c>public</c> schema.</param>
/// <param name="Filter">The PostgREST-style filter Realtime applies (<c>column=eq.value</c>).</param>
public readonly record struct RealtimeBinding(string Table, string Filter)
{
    /// <summary>The schema both tables live in.</summary>
    public const string Schema = "public";

    /// <summary>Every change: <c>INSERT</c>, <c>UPDATE</c> and <c>DELETE</c>.</summary>
    public const string AnyEvent = "*";

    /// <summary>The exports table.</summary>
    public const string ExportsTable = "bundle_exports";

    /// <summary>The orders table.</summary>
    public const string OrdersTable = "orders";

    /// <summary>The exports that target this connection: live delivery.</summary>
    public static RealtimeBinding Exports(string connectionId)
        => new(ExportsTable, "target_connection_id=eq." + connectionId);

    /// <summary>One order's row, watched while its bundle is still building.</summary>
    public static RealtimeBinding Order(string orderId)
        => new(OrdersTable, "id=eq." + orderId);
}

/// <summary>
/// The channel's names and the client's few decisions: where the socket is, whether it may open at
/// all, which topic is the curator's, which error is final, and how long to wait before trying again.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>No location or key is compiled in.</b> The platform has not published where a plugin's Realtime
/// socket is or which publishable key opens it (the contract's open point), so both come from local
/// configuration, and with either missing the connected host stays off rather than guessing.
/// </para>
/// </remarks>
public static class RealtimeChannel
{
    /// <summary>The Phoenix protocol version the frames are written in: JSON objects, not arrays.</summary>
    public const string ProtocolVersion = "1.0.0";

    /// <summary>The socket's own topic, which heartbeats ride.</summary>
    public const string SocketTopic = "phoenix";

    /// <summary>The prefix Realtime puts on a channel's name on the wire.</summary>
    public const string WirePrefix = "realtime:";

    // Supabase's own clients wait 1, 2, 5 and then 10 seconds between attempts; this one does the same,
    // so a platform tuned for them sees nothing new.
    private static readonly int[] BackoffMs = [1_000, 2_000, 5_000, 10_000];

    /// <summary>The curator's topic, <c>vault:&lt;auth user id&gt;</c>.</summary>
    public static string Topic(string userId) => "vault:" + userId;

    /// <summary>The topic as frames name it: <c>realtime:vault:&lt;auth user id&gt;</c>.</summary>
    public static string WireTopic(string userId) => WirePrefix + Topic(userId);

    /// <summary>
    /// The socket to open: the configured location's <c>websocket</c> endpoint with the publishable key
    /// and the protocol version, or <c>null</c> when either is missing or the location is not a socket.
    /// </summary>
    /// <remarks>
    /// The location is the Realtime base (<c>wss://…/realtime/v1</c>); <c>http</c> and <c>https</c> are
    /// read as <c>ws</c> and <c>wss</c>, as Supabase's clients read them, and a location that already
    /// ends in <c>/websocket</c> is taken as it is.
    /// </remarks>
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

    /// <summary>
    /// Why the connected host is off, as one log line, or <c>null</c> when it may connect. The two
    /// configuration keys are named, because they are what a maintainer sets.
    /// </summary>
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

    /// <summary>
    /// The signed-in curator's auth user id: the access token's <c>sub</c> claim, read and not verified
    /// (the platform verifies it at the join). <c>null</c> when the token is not a JWT or names no subject.
    /// </summary>
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

    /// <summary>
    /// Whether a channel error is final: the session does not own the topic. Every other channel error
    /// is transient and retried with <see cref="Backoff"/>.
    /// </summary>
    public static bool IsRefusal(string? reason)
        => (reason ?? string.Empty).Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);

    /// <summary>The wait before the <paramref name="attempt"/>th try again, counted from 1.</summary>
    public static TimeSpan Backoff(int attempt)
        => TimeSpan.FromMilliseconds(BackoffMs[Math.Clamp(attempt, 1, BackoffMs.Length) - 1]);
}

/// <summary>What an incoming frame is, as far as the client acts on it.</summary>
public enum RealtimeMessageKind
{
    /// <summary>An answer to a push (<c>phx_reply</c>), matched by its ref.</summary>
    Reply,

    /// <summary>A row change on a subscribed table (<c>postgres_changes</c>).</summary>
    RowChange,

    /// <summary>The channel failed: <c>phx_error</c>, or a <c>system</c> message in error.</summary>
    ChannelError,

    /// <summary>The platform closed the channel (<c>phx_close</c>).</summary>
    Close,

    /// <summary>Well formed, and nothing to act on: presence state and diffs, a system ok.</summary>
    Ignored,

    /// <summary>Not a frame this client can read.</summary>
    Unreadable,
}

/// <summary>A row change as Realtime carries it.</summary>
/// <param name="Table">The table changed.</param>
/// <param name="Type"><c>INSERT</c>, <c>UPDATE</c> or <c>DELETE</c>.</param>
/// <param name="RecordJson">The row after the change, as JSON with snake_case columns.</param>
/// <param name="OldRecordJson">The row before, as far as the platform sends it; <c>{}</c> when it sends none.</param>
public sealed record RealtimeRowChange(string Table, string Type, string RecordJson, string OldRecordJson)
{
    /// <summary>A change to an export: read <see cref="RecordJson"/> with <see cref="HostExports.TryParse"/>.</summary>
    public bool IsExport => string.Equals(Table, RealtimeBinding.ExportsTable, StringComparison.Ordinal);

    /// <summary>A change to an order: read <see cref="RecordJson"/> with <see cref="BundleReadiness.IsReadyRow"/>.</summary>
    public bool IsOrder => string.Equals(Table, RealtimeBinding.OrdersTable, StringComparison.Ordinal);
}

/// <summary>One incoming frame, decoded.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Topic">The topic it is on.</param>
/// <param name="Event">Its event name, as sent.</param>
/// <param name="Ref">The push it answers, or <c>null</c>.</param>
/// <param name="JoinRef">The join it belongs to, or <c>null</c>.</param>
/// <param name="Ok">For a reply, whether its status was <c>ok</c>.</param>
/// <param name="Reason">For a reply in error or a channel error, the platform's words.</param>
/// <param name="Change">For a row change, the change.</param>
/// <param name="PayloadJson">The payload as sent, for the log.</param>
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

/// <summary>
/// The Realtime (Phoenix) frames, encoded and decoded: join, presence track and untrack, leave,
/// heartbeat and access-token refresh out; replies, row changes, channel errors and closes in.
/// </summary>
/// <remarks>
/// <para>
/// Spoken directly rather than through a Supabase SDK, so the plugin's assemblies take no package:
/// an SDK would bring its own JSON and HTTP assemblies into Revit's process, where another add-in's
/// copy decides which one loads. The frames are the Phoenix JSON serializer's,
/// <c>{"topic","event","payload","ref","join_ref"}</c>, at <see cref="RealtimeChannel.ProtocolVersion"/>.
/// </para>
/// <para>
/// Every frame here is pure, so the wire is pinned headlessly; <c>RealtimeClient</c> in
/// <c>MantlePlace.Revit.Client</c> holds only the socket and the order of things.
/// </para>
/// </remarks>
public static class RealtimeFrames
{
    /// <summary>
    /// Joins the curator's channel: private, presence keyed by the connection id, authorised with the
    /// session's access token, and subscribed to the given row changes. Its ref is its join ref.
    /// </summary>
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

    /// <summary>Tracks (or re-tracks) the presence entry, its payload written verbatim.</summary>
    public static string Track(string wireTopic, string joinRef, string reference, string presenceJson)
        => Frame(wireTopic, "presence", reference, joinRef, writer =>
        {
            writer.WriteString("type", "presence");
            writer.WriteString("event", "track");
            writer.WritePropertyName("payload");
            writer.WriteRawValue(presenceJson);
        });

    /// <summary>Takes the presence entry down.</summary>
    public static string Untrack(string wireTopic, string joinRef, string reference)
        => Frame(wireTopic, "presence", reference, joinRef, writer =>
        {
            writer.WriteString("type", "presence");
            writer.WriteString("event", "untrack");
        });

    /// <summary>Leaves the channel.</summary>
    public static string Leave(string wireTopic, string joinRef, string reference)
        => Frame(wireTopic, "phx_leave", reference, joinRef, _ => { });

    /// <summary>Re-authorises the joined channel with a refreshed access token.</summary>
    public static string AccessToken(string wireTopic, string joinRef, string reference, string accessToken)
        => Frame(wireTopic, "access_token", reference, joinRef, writer => writer.WriteString("access_token", accessToken));

    /// <summary>Keeps the socket open; Realtime closes one that goes quiet.</summary>
    public static string Heartbeat(string reference)
        => Frame(RealtimeChannel.SocketTopic, "heartbeat", reference, null, _ => { });

    /// <summary>Decodes one incoming frame. Never throws: a frame it cannot read is <see cref="RealtimeMessageKind.Unreadable"/>.</summary>
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

    // A refused join answers `{ "reason": "..." }`; Realtime has also been seen to put the words under
    // `response` as a bare string, or as `message`.
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

    /// <summary>A ref as text: refs are counters, written in the invariant culture.</summary>
    public static string RefText(long counter) => counter.ToString(CultureInfo.InvariantCulture);
}
