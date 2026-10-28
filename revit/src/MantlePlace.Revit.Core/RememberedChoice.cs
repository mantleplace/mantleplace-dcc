using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What the curator ticked the last time they pressed Import on this machine: the next import window
/// opens on it.
/// </summary>
/// <remarks>
/// <para>
/// One entry per layer the last bundle carried, ticked or not, and a level for each row that offered
/// levels. A layer the last bundle did not carry has no entry, and the checklist falls back to that
/// layer's default for it (<see cref="ImportChecklist.Restore"/>).
/// </para>
/// <para>
/// Pure: the bytes in and out are <see cref="Parse"/> and <see cref="ToBytes"/>, and the file they
/// live in is the client's (<c>ImportChoiceStore</c>). A file that is empty, not JSON, not an object,
/// or of another <see cref="Shape"/> is no remembered choice, never an error.
/// </para>
/// </remarks>
public sealed class RememberedChoice
{
    /// <summary>The file's shape. A file with another number, or none, is an older or newer shape and is ignored.</summary>
    public const int Shape = 1;

    private readonly Dictionary<ImportLayer, bool> _ticked;
    private readonly Dictionary<ImportLayer, FidelityLevel> _levels;

    private RememberedChoice(Dictionary<ImportLayer, bool> ticked, Dictionary<ImportLayer, FidelityLevel> levels)
    {
        _ticked = ticked;
        _levels = levels;
    }

    /// <summary>
    /// What was pressed: for every layer the checklist offered, whether it was imported, and the level
    /// of each row that lists levels.
    /// </summary>
    /// <remarks>
    /// Reads only what a checklist never changes after it is made (<see cref="ImportChecklist.Layers"/>,
    /// <see cref="ImportChecklist.HasLevels"/>) and the choice itself, so it is safe to call from a
    /// thread that is not the window's.
    /// </remarks>
    public static RememberedChoice Of(ImportChecklist offered, ImportLayerChoice pressed)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(pressed);

        return new RememberedChoice(
            offered.Layers.ToDictionary(layer => layer, pressed.Includes),
            offered.Layers.Where(offered.HasLevels).ToDictionary(layer => layer, pressed.LevelOf));
    }

    /// <summary>Whether the last import ticked <paramref name="layer"/>, or <c>null</c> when it did not offer it.</summary>
    public bool? Ticked(ImportLayer layer) => _ticked.TryGetValue(layer, out bool ticked) ? ticked : null;

    /// <summary>The level the last import chose for <paramref name="layer"/>, or <c>null</c> when it chose none.</summary>
    public FidelityLevel? Level(ImportLayer layer) => _levels.TryGetValue(layer, out FidelityLevel level) ? level : null;

    /// <summary>Reads a remembered choice, or <c>null</c> for bytes that are not one.</summary>
    public static RememberedChoice? Parse(byte[]? bytes)
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

            Dictionary<ImportLayer, bool> ticked = [];
            if (root.TryGetProperty("layers", out JsonElement layers) && layers.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in layers.EnumerateObject())
                {
                    if (Enum.TryParse(entry.Name, ignoreCase: false, out ImportLayer layer)
                        && Enum.IsDefined(layer)
                        && entry.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        ticked[layer] = entry.Value.GetBoolean();
                    }
                }
            }

            Dictionary<ImportLayer, FidelityLevel> levels = [];
            if (root.TryGetProperty("levels", out JsonElement chosen) && chosen.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in chosen.EnumerateObject())
                {
                    if (Enum.TryParse(entry.Name, ignoreCase: false, out ImportLayer layer)
                        && Enum.IsDefined(layer)
                        && entry.Value.ValueKind == JsonValueKind.String
                        && FidelityLevelNames.TryParse(entry.Value.GetString(), out FidelityLevel level))
                    {
                        levels[layer] = level;
                    }
                }
            }

            return new RememberedChoice(ticked, levels);
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
            json.WriteStartObject("layers");
            foreach (KeyValuePair<ImportLayer, bool> entry in _ticked.OrderBy(entry => entry.Key))
            {
                json.WriteBoolean(entry.Key.ToString(), entry.Value);
            }

            json.WriteEndObject();
            json.WriteStartObject("levels");
            foreach (KeyValuePair<ImportLayer, FidelityLevel> entry in _levels.OrderBy(entry => entry.Key))
            {
                json.WriteString(entry.Key.ToString(), FidelityLevelNames.Token(entry.Value));
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
