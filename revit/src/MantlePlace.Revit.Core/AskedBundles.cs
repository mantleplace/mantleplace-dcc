using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The bundles the import prompt has already asked about on this machine (<c>HPS-56</c>): the memory
/// that keeps <see cref="ImportFeedbackPrompt"/> to once per bundle, and keeps it quiet for good after
/// the curator has seen it.
/// </summary>
/// <remarks>
/// <para>
/// Immutable. A bundle is remembered the moment it is asked about, not when it is answered, so a
/// line dismissed, ignored or closed with the window never comes back for that bundle.
/// </para>
/// <para>
/// Bounded at <see cref="Capacity"/>: past it the oldest bundle is forgotten, which at worst asks about a
/// bundle imported long ago once more. Pure: the bytes in and out are <see cref="Parse"/> and
/// <see cref="ToBytes"/>, and the file they live in is the client's (<c>FeedbackAskedStore</c>). A file that
/// is empty, not JSON, not an object, or of another <see cref="Shape"/> is no memory, never an error.
/// </para>
/// </remarks>
public sealed class AskedBundles
{
    /// <summary>The file's shape. A file with another number, or none, is an older or newer shape and is ignored.</summary>
    public const int Shape = 1;

    /// <summary>The most bundles remembered.</summary>
    public const int Capacity = 500;

    private readonly List<string> _orders;

    private AskedBundles(List<string> orders) => _orders = orders;

    /// <summary>No bundle asked about yet.</summary>
    public static AskedBundles Empty { get; } = new([]);

    /// <summary>How many bundles are remembered.</summary>
    public int Count => _orders.Count;

    /// <summary>Whether <paramref name="orderId"/> has been asked about. An id that is not a UUID never has.</summary>
    public bool Contains(string? orderId)
        => FeedbackLink.OrderIdOf(orderId) is { } order && _orders.Contains(order, StringComparer.Ordinal);

    /// <summary>
    /// This memory with <paramref name="orderId"/> added, or this one when it is already there or is not a
    /// UUID.
    /// </summary>
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

    /// <summary>Reads a memory, or <c>null</c> for bytes that are not one.</summary>
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

    /// <summary>The file's bytes: <see cref="Parse"/> reads them back.</summary>
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
