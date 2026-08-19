using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// Total accessors over <see cref="JsonElement"/>: absent, null, or wrong-typed all yield the
/// stated default instead of throwing.
/// </summary>
/// <remarks>
/// The manifest reader must never throw on a malformed document — a refusal carries a message the
/// user can act on, an exception carries a stack trace they cannot. Keeping the "did not throw"
/// discipline in one place is what makes that reviewable.
/// <para>
/// Note the deliberate split between <see cref="Number"/> (absent ⇒ a stated fallback) and
/// <see cref="OptionalInt"/> / <see cref="OptionalDouble"/> (absent ⇒ <c>null</c>, meaning
/// <em>unknown</em>). Optional integrity and count facts use the second form: a <c>null</c> on the
/// wire must not become a <c>0</c> that a later comparison treats as real (HPS-20).
/// </para>
/// </remarks>
internal static class JsonReading
{
    internal static JsonElement? Object(this JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child)
            || child.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return child;
    }

    internal static JsonElement? Array(this JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child)
            || child.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return child;
    }

    internal static string Str(this JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child)
            || child.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return child.GetString() ?? string.Empty;
    }

    /// <summary>A string field, or <c>null</c> when absent/blank — for "unknown" semantics.</summary>
    internal static string? OptionalStr(this JsonElement parent, string field)
    {
        string value = parent.Str(field);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static bool Bool(this JsonElement parent, string field, bool fallback = false)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child))
        {
            return fallback;
        }

        return child.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    internal static double Number(this JsonElement parent, string field, double fallback = 0.0)
        => parent.OptionalDouble(field) ?? fallback;

    internal static double? OptionalDouble(this JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child))
        {
            return null;
        }

        if (child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out double value))
        {
            return value;
        }

        // The ETL occasionally serialises a numeric as a string; parse invariantly rather than
        // silently reading zero.
        if (child.ValueKind == JsonValueKind.String
            && double.TryParse(child.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return null;
    }

    internal static int? OptionalInt(this JsonElement parent, string field)
    {
        double? value = parent.OptionalDouble(field);
        return value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    internal static int Int(this JsonElement parent, string field, int fallback = 0)
        => parent.OptionalInt(field) ?? fallback;

    /// <summary>
    /// An integer field read by TRUNCATION, clamped to <see cref="int"/>. Used for the manifest
    /// version, where the Unreal reference truncates and the two hosts must not disagree about
    /// which side of the floor a non-integral value falls on.
    /// </summary>
    internal static int TruncatedInt(this JsonElement parent, string field, int fallback = 0)
    {
        double? value = parent.OptionalDouble(field);
        if (value is null || double.IsNaN(value.Value))
        {
            return fallback;
        }

        double truncated = Math.Truncate(value.Value);
        if (truncated <= int.MinValue)
        {
            return int.MinValue;
        }

        return truncated >= int.MaxValue ? int.MaxValue : (int)truncated;
    }

    /// <summary>True when <paramref name="field"/> exists and is an object (empty counts).</summary>
    internal static bool HasObject(this JsonElement parent, string field)
        => parent.Object(field) is not null;
}
