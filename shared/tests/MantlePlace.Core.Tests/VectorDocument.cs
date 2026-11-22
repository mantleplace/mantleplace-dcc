using System.Text.Json;

namespace MantlePlace.Core.Tests;

internal sealed class VectorDocument : IDisposable
{
    private static readonly HashSet<string> ProseKeys = new(StringComparer.Ordinal)
    {
        "$comment",
        "note",
        "reason",
        "rule",
        "definition",
        "alphabet",
        "formula",
        "boundary",
        "default",
        "collisionSuffix",
    };

    private readonly JsonDocument _document;
    private readonly HashSet<string> _leaves = new(StringComparer.Ordinal);
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    private VectorDocument(JsonDocument document)
    {
        _document = document;
        Collect(document.RootElement, string.Empty);
        Root = new VectorNode(this, document.RootElement, string.Empty);
    }

    internal VectorNode Root { get; }

    internal static VectorDocument Parse(string payload) => new(JsonDocument.Parse(payload));

    internal IReadOnlyList<string> UnreadPaths()
    {
        List<string> unread = [];
        foreach (string leaf in _leaves)
        {
            if (!_read.Contains(leaf) && !IsProse(leaf))
            {
                unread.Add(leaf);
            }
        }

        unread.Sort(StringComparer.Ordinal);
        return unread;
    }

    public void Dispose() => _document.Dispose();

    internal void MarkRead(string path) => _read.Add(path);

    internal void MarkSubtreeRead(string path)
    {
        foreach (string leaf in _leaves)
        {
            if (leaf.Equals(path, StringComparison.Ordinal)
                || leaf.StartsWith(path + ".", StringComparison.Ordinal)
                || leaf.StartsWith(path + "[", StringComparison.Ordinal))
            {
                _read.Add(leaf);
            }
        }
    }

    private void Collect(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Collect(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name);
                }

                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Collect(item, $"{path}[{index}]");
                    index++;
                }

                break;

            default:
                _leaves.Add(path);
                break;
        }
    }

    private static bool IsProse(string path)
    {
        int lastDot = path.LastIndexOf('.');
        string key = lastDot < 0 ? path : path[(lastDot + 1)..];

        int bracket = key.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0)
        {
            key = key[..bracket];
        }

        return ProseKeys.Contains(key) || key.EndsWith("Note", StringComparison.Ordinal);
    }
}

internal sealed class VectorNode
{
    private readonly VectorDocument _owner;
    private readonly string _path;

    internal VectorNode(VectorDocument owner, JsonElement element, string path)
    {
        _owner = owner;
        Element = element;
        _path = path;
    }

    internal JsonElement Element { get; }

    internal string Path => _path;

    internal bool Has(string key) => Element.TryGetProperty(key, out _);

    internal VectorNode? Obj(string key)
        => Element.TryGetProperty(key, out JsonElement child) && child.ValueKind == JsonValueKind.Object
            ? new VectorNode(_owner, child, Child(key))
            : null;

    internal IReadOnlyList<VectorNode> Items(string key)
    {
        if (!Element.TryGetProperty(key, out JsonElement child) || child.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<VectorNode> items = [];
        int index = 0;
        foreach (JsonElement item in child.EnumerateArray())
        {
            items.Add(new VectorNode(_owner, item, $"{Child(key)}[{index}]"));
            index++;
        }

        return items;
    }

    internal string? Str(string key) => Read(key, JsonValueKind.String, out JsonElement child)
        ? child.GetString()
        : null;

    internal int? Int(string key) => Read(key, JsonValueKind.Number, out JsonElement child)
        ? child.GetInt32()
        : null;

    internal double? Double(string key) => Read(key, JsonValueKind.Number, out JsonElement child)
        ? child.GetDouble()
        : null;

    internal string? Version(string key)
    {
        if (Read(key, JsonValueKind.String, out JsonElement text))
        {
            return text.GetString();
        }

        return Read(key, JsonValueKind.Number, out JsonElement number)
            ? number.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    internal IReadOnlyList<string> Keys()
    {
        if (Element.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<string> names = [];
        foreach (JsonProperty property in Element.EnumerateObject())
        {
            names.Add(property.Name);
        }

        return names;
    }

    internal bool? Bool(string key)
    {
        if (!Element.TryGetProperty(key, out JsonElement child))
        {
            return null;
        }

        if (child.ValueKind == JsonValueKind.Null)
        {
            _owner.MarkRead(Child(key));
            return null;
        }

        if (child.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        _owner.MarkRead(Child(key));
        return child.ValueKind == JsonValueKind.True;
    }

    internal bool IsNull(string key)
    {
        if (!Element.TryGetProperty(key, out JsonElement child) || child.ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        _owner.MarkRead(Child(key));
        return true;
    }

    private bool Read(string key, JsonValueKind wanted, out JsonElement child)
    {
        if (!Element.TryGetProperty(key, out child))
        {
            return false;
        }

        if (child.ValueKind == JsonValueKind.Null)
        {
            _owner.MarkRead(Child(key));
            return false;
        }

        if (child.ValueKind != wanted)
        {
            return false;
        }

        _owner.MarkRead(Child(key));
        return true;
    }

    internal string? AsString()
    {
        if (Element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        _owner.MarkRead(_path);
        return Element.GetString();
    }

    internal void MarkConsumed(string key) => _owner.MarkSubtreeRead(Child(key));

    private string Child(string key) => _path.Length == 0 ? key : _path + "." + key;
}
