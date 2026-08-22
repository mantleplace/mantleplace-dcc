using System.Text.Json;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// A corpus <c>vector</c> file, with every value it contains tracked until something reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> <c>HPS-46</c> proves consumption through <c>expectations</c> keys:
/// every key an executed case declares must be READ by a typed assertion, so an unknown key, a
/// wrong-typed key and a never-ran assertion all fail identically. But a case whose <c>expect</c> is
/// <c>vector</c> carries no <c>expectations</c> at all — the file itself is the payload. Of the
/// cases this host claims, eleven are vectors, and for <c>auth</c>, <c>cache</c> and <c>digest</c>
/// it is seven of seven: exactly where the ⛔ rules live. A suite that drives one row of an
/// eleven-row truth table passes, and the coverage ratchet records the case as covered.
/// </para>
/// <para>
/// So the same idea is applied one level down. Every leaf path in the file is enumerated up front;
/// each typed read strikes one off; anything still standing at the end fails the suite. Driving
/// <c>stateValidation[0]</c> and forgetting <c>stateValidation[3]</c> — the empty-expected-state row,
/// the one ⛔<c>HPS-07</c> exists for — is now a failure with the path in the message.
/// </para>
/// <para>
/// Proposed upstream as an <c>HPS-46</c> amendment rather than implemented quietly; until it is
/// normative it is this host's own stricter reading, which costs other hosts nothing.
/// </para>
/// </remarks>
internal sealed class VectorDocument : IDisposable
{
    /// <summary>
    /// Keys whose values are normative PROSE for a human reading the corpus, not vectors for a host
    /// to assert.
    /// </summary>
    /// <remarks>
    /// The corpus has no marker for this today, so the set is enumerated. The <c>HPS-46</c>
    /// amendment proposes a convention — a <c>$</c> prefix or a <c>Note</c> suffix — after which
    /// this collapses to two rules and stops needing maintenance. Anything NOT here has to be read
    /// by something, which is the point: a new key nobody asserts fails loudly rather than sitting
    /// in the file looking covered.
    /// </remarks>
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

    /// <summary>Leaf paths nothing read, excluding prose. A non-empty result is a failure.</summary>
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

    /// <summary>Marks a whole subtree read — for a payload handed to the parser under test verbatim.</summary>
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

    /// <summary>Whether a leaf path's own key — index stripped — is documentation.</summary>
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

/// <summary>One node inside a <see cref="VectorDocument"/>. Reads are recorded.</summary>
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

    /// <summary>The elements of an array-valued property. Empty when it is not an array.</summary>
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

    /// <summary>
    /// Reads a string, recording the path. <c>null</c> when the key is absent, is not a string, or
    /// is an explicit JSON <c>null</c>.
    /// </summary>
    /// <remarks>
    /// An explicit <c>null</c> counts as READ, because in this corpus it is a value: the
    /// <c>sha256: null</c> row of the cache truth table is the one ⛔<c>HPS-27</c> exists for
    /// (unknown ≠ absent), and a tracker that treated JSON null as nothing to track would make it
    /// the single row a suite could skip for free. An absent key and a wrong-typed one still mark
    /// nothing — those are the two the tracker has to catch.
    /// </remarks>
    internal string? Str(string key) => Read(key, JsonValueKind.String, out JsonElement child)
        ? child.GetString()
        : null;

    internal int? Int(string key) => Read(key, JsonValueKind.Number, out JsonElement child)
        ? child.GetInt32()
        : null;

    internal double? Double(string key) => Read(key, JsonValueKind.Number, out JsonElement child)
        ? child.GetDouble()
        : null;

    /// <summary>
    /// Reads a manifest version in EITHER family, recording the path: a semver string as itself, an
    /// integer-era number stringified.
    /// </summary>
    /// <remarks>
    /// The cache truth table deliberately spans the era break — its at-the-floor rows carry the
    /// string "1.0.0" and its too-old rows carry the number 19, the pre-history's top being the
    /// neighbour immediately below a semver floor in the total order. Reading with <see cref="Str"/>
    /// alone would leave the too-old rows unread, and the leaf tracker would then fail the case for
    /// an unasserted key rather than silently skipping it — correct, but the fix belongs here.
    /// </remarks>
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

    /// <summary>Property names of this node, when it is an object used as a map.</summary>
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

    /// <summary>Whether the key is present and explicitly <c>null</c>. Records the path.</summary>
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

    /// <summary>Reads this node as a scalar string — for an element of a string array.</summary>
    internal string? AsString()
    {
        if (Element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        _owner.MarkRead(_path);
        return Element.GetString();
    }

    /// <summary>
    /// Marks a property's whole subtree read, for a value handed to the parser under test verbatim
    /// rather than field by field.
    /// </summary>
    internal void MarkConsumed(string key) => _owner.MarkSubtreeRead(Child(key));

    private string Child(string key) => _path.Length == 0 ? key : _path + "." + key;
}
