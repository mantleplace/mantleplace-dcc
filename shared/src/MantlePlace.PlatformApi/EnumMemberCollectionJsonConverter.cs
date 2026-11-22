using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MantlePlace.PlatformApi;

public sealed class EnumMemberCollectionJsonConverter<T> : JsonConverter<ICollection<T>> where T : struct, Enum
{
    private static readonly EnumMemberJsonConverter<T> Item = new();

    public override ICollection<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array of {typeof(T).Name}.");
        }

        Collection<T> items = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            items.Add(Item.Read(ref reader, typeof(T), options));
        }

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException($"The array of {typeof(T).Name} is not closed.");
        }

        return items;
    }

    public override void Write(Utf8JsonWriter writer, ICollection<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartArray();
        foreach (T item in value)
        {
            Item.Write(writer, item, options);
        }

        writer.WriteEndArray();
    }
}
