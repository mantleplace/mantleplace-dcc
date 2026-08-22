using System.Text.Json;

namespace MantlePlace.Revit.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

/// <summary>
/// One node inside a case's <c>expectations</c>, with every typed read recorded by path (HPS-46b).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> <c>HPS-46</c> proves consumption at the top level of
/// <c>expectations</c>: a declared key nothing read fails the suite. It stops there. Asserting that
/// <c>items</c> was read says nothing about the thirty-four leaves inside its two rows, so a host
/// reading one of them satisfies the rule in full and nothing can tell it apart from a host reading
/// all of them — the same blind spot one level down.
/// </para>
/// <para>
/// So the same idea is applied below the top level, the way <see cref="VectorDocument"/> applies it
/// inside a <c>vector</c> file: every nested leaf is enumerated up front by
/// <see cref="ConformanceCorpus.NestedExpectationPaths"/>, each typed read here strikes one off, and
/// anything still standing fails with its path.
/// </para>
/// <para>
/// Deliberately NOT the production <c>JsonReading</c> accessors, even though they are a few metres
/// away and do the same thing: a conformance suite that reads its expected values through the parser
/// under test's own helpers cannot catch a bug in those helpers, because the two sides would agree
/// by construction.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Reads a string, recording the path. <c>null</c> when the key is absent, is not a string, or
    /// is an explicit JSON <c>null</c>.
    /// </summary>
    /// <remarks>
    /// An explicit <c>null</c> counts as READ, because in this corpus it is a value: the legacy
    /// row's nulls mean UNKNOWN, which is the whole point of ⛔<c>HPS-27</c>, and a tracker that
    /// treated JSON null as nothing to track would make those the leaves a suite could skip for
    /// free. An absent key and a wrong-typed one record nothing — those are the two the tracker has
    /// to catch, and they must fail identically.
    /// </remarks>
    internal string? Str(string key) => Read(key, JsonValueKind.String, out JsonElement child)
        ? child.GetString()
        : null;

    internal double? Double(string key) => Read(key, JsonValueKind.Number, out JsonElement child)
        ? child.GetDouble()
        : null;

    internal int? Int(string key) => Read(key, JsonValueKind.Number, out JsonElement child)
        ? child.GetInt32()
        : null;

    /// <summary>
    /// Reads a manifest version in EITHER family, recording the path: a semver string as itself, an
    /// integer-era number stringified. The vault lists bundles at rest, so a corpus expectation for
    /// a listed version may be written in either era.
    /// </summary>
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

    /// <summary>
    /// An object-valued key as a node, or <c>null</c> when it is absent or not an object. An EMPTY
    /// object is recorded here rather than by a child read, because it has no children — "known to
    /// hold nothing" is still an assertion the host has to make.
    /// </summary>
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

    /// <summary>
    /// The elements of an array-valued key as nodes, or <c>null</c> when it is absent or not an
    /// array. An EMPTY array records its own path, for the reason <see cref="Object"/> gives:
    /// <c>formats: []</c> on the legacy row is the assertion that the row has none.
    /// </summary>
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

    /// <summary>Reads this node as a scalar string — for an element of a string array.</summary>
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
