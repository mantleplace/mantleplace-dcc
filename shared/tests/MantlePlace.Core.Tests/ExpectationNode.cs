using System.Text.Json;

namespace MantlePlace.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

internal sealed class ExpectationNode
{
    private readonly CorpusCase _case;
    private readonly string _path;

    internal ExpectationNode(CorpusCase corpusCase, JsonElement element, string path)
    {
        _case = corpusCase;
        Element = element;
        _path = path;
    }

    internal JsonElement Element { get; }

    internal string? Str(string key) => Read(key, JsonValueKind.String, out JsonElement child)
        ? child.GetString()
        : null;

    internal double? Double(string key) => Read(key, JsonValueKind.Number, out JsonElement child)
        ? child.GetDouble()
        : null;

    internal int? Int(string key) => Read(key, JsonValueKind.Number, out JsonElement child)
        ? child.GetInt32()
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

    internal bool? Bool(string key)
    {
        if (!Element.TryGetProperty(key, out JsonElement child))
        {
            return null;
        }

        if (child.ValueKind == JsonValueKind.Null)
        {
            _case.AssertedPaths.Add(Child(key));
            return null;
        }

        if (child.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        _case.AssertedPaths.Add(Child(key));
        return child.ValueKind == JsonValueKind.True;
    }

    internal ExpectationNode? Object(string key)
    {
        if (!Element.TryGetProperty(key, out JsonElement child)
            || child.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!child.EnumerateObject().Any())
        {
            _case.AssertedPaths.Add(Child(key));
        }

        return new ExpectationNode(_case, child, Child(key));
    }

    internal IReadOnlyList<ExpectationNode>? Items(string key)
    {
        if (!Element.TryGetProperty(key, out JsonElement child)
            || child.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<ExpectationNode> items = [];
        int index = 0;
        foreach (JsonElement item in child.EnumerateArray())
        {
            items.Add(new ExpectationNode(_case, item, $"{Child(key)}[{index}]"));
            index++;
        }

        if (items.Count == 0)
        {
            _case.AssertedPaths.Add(Child(key));
        }

        return items;
    }

    internal string? AsString()
    {
        if (Element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        _case.AssertedPaths.Add(_path);
        return Element.GetString();
    }

    private bool Read(string key, JsonValueKind wanted, out JsonElement child)
    {
        if (!Element.TryGetProperty(key, out child))
        {
            return false;
        }

        if (child.ValueKind == JsonValueKind.Null)
        {
            _case.AssertedPaths.Add(Child(key));
            return false;
        }

        if (child.ValueKind != wanted)
        {
            return false;
        }

        _case.AssertedPaths.Add(Child(key));
        return true;
    }

    private string Child(string key) => _path.Length == 0 ? key : $"{_path}.{key}";
}
