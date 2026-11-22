using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Core;

public static class JsonReading
{
    public static JsonElement? Object(this JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child)
            || child.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return child;
    }

    public static JsonElement? Array(this JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child)
            || child.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return child;
    }

    public static string Str(this JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child)
            || child.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return child.GetString() ?? string.Empty;
    }

    public static string? OptionalStr(this JsonElement parent, string field)
    {
        string value = parent.Str(field);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static IReadOnlyList<string> StringArray(this JsonElement parent, string field)
    {
        if (parent.Array(field) is not { } array)
        {
            return [];
        }

        List<string> values = [];
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
            {
                values.Add(text);
            }
        }

        return values;
    }

    public static string? RawNumber(this JsonElement parent, string field)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(field, out JsonElement value)
           && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;

    public static bool Bool(this JsonElement parent, string field, bool fallback = false)
        => parent.OptionalBool(field) ?? fallback;

    public static bool? OptionalBool(this JsonElement parent, string field)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(field, out JsonElement child))
        {
            return null;
        }

        return child.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static double Number(this JsonElement parent, string field, double fallback = 0.0)
        => parent.OptionalDouble(field) ?? fallback;

    public static double? OptionalDouble(this JsonElement parent, string field)
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

        if (child.ValueKind == JsonValueKind.String
            && double.TryParse(child.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return null;
    }

    public static int? OptionalInt(this JsonElement parent, string field)
    {
        double? value = parent.OptionalDouble(field);
        return value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    public static int Int(this JsonElement parent, string field, int fallback = 0)
        => parent.OptionalInt(field) ?? fallback;

    public static int TruncatedInt(this JsonElement parent, string field, int fallback = 0)
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

    public static bool HasObject(this JsonElement parent, string field)
        => parent.Object(field) is not null;
}
