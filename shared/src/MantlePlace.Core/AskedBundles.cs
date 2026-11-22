using System.Text.Json;

namespace MantlePlace.Core;

public sealed class AskedBundles
{
    public const int Shape = 1;

    public const int Capacity = 500;

    private readonly List<string> _orders;

    private AskedBundles(List<string> orders) => _orders = orders;

    public static AskedBundles Empty { get; } = new([]);

    public int Count => _orders.Count;

    public bool Contains(string? orderId)
        => FeedbackLink.OrderIdOf(orderId) is { } order && _orders.Contains(order, StringComparer.Ordinal);

    public AskedBundles With(string? orderId)
    {
        if (FeedbackLink.OrderIdOf(orderId) is not { } order || _orders.Contains(order, StringComparer.Ordinal))
        {
            return this;
        }

        List<string> next = [.. _orders, order];
        if (next.Count > Capacity)
        {
            next.RemoveRange(0, next.Count - Capacity);
        }

        return new AskedBundles(next);
    }

    public static AskedBundles? Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("shape", out JsonElement shape)
                || shape.ValueKind != JsonValueKind.Number
                || !shape.TryGetInt32(out int number)
                || number != Shape)
            {
                return null;
            }

            AskedBundles asked = Empty;
            if (root.TryGetProperty("asked", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        asked = asked.With(entry.GetString());
                    }
                }
            }

            return asked;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public byte[] ToBytes()
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("shape", Shape);
            json.WriteStartArray("asked");
            foreach (string order in _orders)
            {
                json.WriteStringValue(order);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
