using System.Text.Json;

namespace MantlePlace.PlatformApi;

public partial class PlatformApiClient
{
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings) =>
        settings.Converters.Add(new EnumMemberJsonConverterFactory());
}
