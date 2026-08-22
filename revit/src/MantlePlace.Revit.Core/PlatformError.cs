using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>A platform error body reduced to what a human and the UI each need.</summary>
/// <param name="Message">The sentence to show.</param>
/// <param name="Code">The machine-readable code (<c>refunded</c>, <c>revoked</c>), or empty.</param>
/// <param name="Detail">The platform's supplementary prose, or empty. See <see cref="Sentence"/>.</param>
public readonly record struct PlatformError(string Message, string Code, string Detail)
{
    /// <summary>The message plus whatever the platform said about WHY, when it said anything.</summary>
    /// <remarks>
    /// A schema rejection answers <c>"Invalid request"</c> and puts the only actionable half — which
    /// field, and what was wrong with it — in a sibling. Dropping that leaves a curator, and the
    /// developer they report to, with two words and nothing to act on. <see cref="Message"/> stays
    /// the <c>HPS-48</c> precedence read, unchanged; this is strictly additive.
    /// </remarks>
    public string Sentence
        => Detail.Length == 0 || Detail == Message ? Message : Message + " — " + Detail;
}

/// <summary>
/// <c>HPS-48</c>: <b>one</b> precedence order for every error body in the plugin.
/// </summary>
/// <remarks>
/// <para>
/// <c>error_description</c>, then <c>msg</c>, then <c>message</c>, then <c>error_code</c>, then
/// <c>error</c> — most-specific human prose first, machine codes last. Showing a curator
/// <c>invalid_grant</c> when prose was available is strictly worse.
/// </para>
/// <para>
/// One type, used by both the auth and the vault parsers, because two parsers with two orders is how
/// two hosts came to show different text for the same 410
/// once. A single-key
/// body cannot tell two orders apart, which is why the corpus carries competing-key vectors and why
/// this host asserts them by removing one field at a time.
/// </para>
/// <para>
/// The machine-readable <c>code</c> is a SEPARATE read and is unaffected by the message order.
/// </para>
/// </remarks>
public static class PlatformErrors
{
    private static readonly string[] MessagePrecedence =
        ["error_description", "msg", "message", "error_code", "error"];

    /// <summary>Reads an error body. <c>false</c> when it states no error at all.</summary>
    public static bool TryRead(JsonElement root, out PlatformError error)
    {
        error = default;

        foreach (string field in MessagePrecedence)
        {
            string message = root.Str(field);
            if (message.Length > 0)
            {
                error = new PlatformError(message, root.Str("code"), ReadDetail(root));
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The platform's explanation of a refusal: its own <c>detail</c> prose, or a schema
    /// rejection's per-field issues rendered as <c>field: reason</c>.
    /// </summary>
    /// <remarks>
    /// The two shapes are alternatives, not a precedence puzzle — a route sends one or the other.
    /// A malformed issue contributes nothing rather than aborting the read: this text is a
    /// diagnostic, and half of it beats refusing to explain at all.
    /// </remarks>
    private static string ReadDetail(JsonElement root)
    {
        string detail = root.Str("detail");
        if (detail.Length > 0)
        {
            return detail;
        }

        if (root.Array("issues") is not { } issues)
        {
            return string.Empty;
        }

        List<string> rendered = [];
        foreach (JsonElement issue in issues.EnumerateArray())
        {
            string reason = issue.Str("message");
            if (reason.Length == 0)
            {
                continue;
            }

            string field = string.Join(".", issue.StringArray("path"));
            rendered.Add(field.Length > 0 ? field + ": " + reason : reason);
        }

        return string.Join("; ", rendered);
    }

    /// <summary>Reads an error body from raw bytes. <c>null</c> when it is not JSON or states none.</summary>
    public static PlatformError? FromBody(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && TryRead(document.RootElement, out PlatformError error)
                    ? error
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
