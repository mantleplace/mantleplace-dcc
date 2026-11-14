using System.Text.Json;

namespace MantlePlace.PlatformApi;

public partial class PlatformApiClient
{
    // The generated client leaves enums inside collections without a converter; see EnumMemberJsonConverter.
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings) =>
        settings.Converters.Add(new EnumMemberJsonConverterFactory());
}
