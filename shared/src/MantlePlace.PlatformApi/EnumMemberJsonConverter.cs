using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MantlePlace.PlatformApi;

public sealed class EnumMemberJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> Names = Build();

    private static readonly Dictionary<string, T> Values = Names.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is not null && Values.TryGetValue(text, out T value))
        {
            return value;
        }

        throw new JsonException($"\"{text}\" is not a {typeof(T).Name} the Platform API serves.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Names.TryGetValue(value, out string? name))
        {
            throw new JsonException($"{value} is not a {typeof(T).Name} the Platform API serves.");
        }

        writer.WriteStringValue(name);
    }

    private static Dictionary<T, string> Build()
    {
        Dictionary<T, string> names = new();
        foreach (FieldInfo field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            EnumMemberAttribute? member = field.GetCustomAttribute<EnumMemberAttribute>();
            names[(T)field.GetValue(null)!] = member?.Value ?? field.Name;
        }

        return names;
    }
}

public sealed class EnumMemberJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter?)Activator.CreateInstance(typeof(EnumMemberJsonConverter<>).MakeGenericType(typeToConvert));
}
