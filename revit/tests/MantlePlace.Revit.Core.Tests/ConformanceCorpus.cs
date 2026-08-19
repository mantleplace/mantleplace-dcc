using System.Text.Json;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Reads the shared conformance corpus at <c>tools/manifest-conformance/corpus/</c>.
/// </summary>
/// <remarks>
/// <para>
/// A C# re-implementation of
/// <c>unreal/Plugins/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h</c>,
/// not a shared one: hosts share specifications and vectors, never code, until a second host of the
/// same language exists (HPS-43).
/// </para>
/// <para>
/// Two behaviours here are normative rather than convenient (HPS-46), and both guard the same
/// failure — a suite that reports green because it asserted nothing (HPS-40):
/// </para>
/// <list type="bullet">
///   <item>a corpus that cannot be found, or a claimed group that resolves to zero cases, is a
///   <em>failure</em>, never a skip;</item>
///   <item>a declared <c>expectations</c> key this host never READ is a failure. Consumption is
///   proven by what was actually ASSERTED: the <c>Wants*</c> accessors record each key on a
///   successful typed read, and a key the case declares that was never recorded — unknown to the
///   host, declared with a type the accessor rejects, or on an assertion path that never ran —
///   fails the suite. An allow-list of key NAMES cannot express this: it catches an unknown key
///   but not <c>"orderId": 999</c>, which asserts nothing while still counting as
///   covered.</item>
/// </list>
/// <para>
/// The reader itself is proven by the self-test corpus at <c>corpus/self-test/</c> (HPS-46):
/// deliberately broken fixtures this reader must REJECT, driven through
/// <see cref="LoadGroup(string, string, out List{CorpusCase})"/> and
/// <see cref="UnindexedCaseFiles"/> by <see cref="ConformanceCorpusSelfTests"/>.
/// </para>
/// </remarks>
internal static class ConformanceCorpus
{
    /// <summary>
    /// The host key this suite claims. Cases carrying a different <c>appliesTo</c> assert another
    /// host's manifest block and are skipped; cases with no <c>appliesTo</c> are host-invariant and
    /// bind everyone (HPS-41).
    /// </summary>
    internal const string HostKey = BundleManifestReader.HostKey;

    private const string CorpusRelativePath = "tools/manifest-conformance/corpus";

    /// <summary>
    /// How far up to walk looking for the repo root. The suite runs from
    /// <c>revit/tests/&lt;project&gt;/bin/&lt;config&gt;/&lt;tfm&gt;</c>, six levels down; the slack absorbs a
    /// different output layout without hardcoding depth.
    /// </summary>
    private const int MaxWalkUp = 10;

    internal sealed class CorpusCase
    {
        internal required string Id { get; init; }

        internal required string Group { get; init; }

        internal required string File { get; init; }

        internal required string Expect { get; init; }

        internal string ErrorContains { get; init; } = string.Empty;

        internal string Reason { get; init; } = string.Empty;

        /// <summary>Raw text of the vector file — what the parser under test is handed.</summary>
        internal required string Payload { get; init; }

        /// <summary>The <c>expectations</c> object, or <c>null</c> when the case declares none.</summary>
        internal JsonElement? Expectations { get; init; }

        /// <summary>
        /// Keys the host actually READ and asserted, recorded by the <c>Wants*</c> accessors.
        /// Tracked rather than assumed: an accessor returns false both for "the case does not
        /// declare this" and for "it declares it with the wrong JSON type", and the second must not
        /// pass silently — a corpus typo like <c>"orderId": 999</c> would otherwise assert nothing
        /// while still counting as covered.
        /// </summary>
        internal HashSet<string> AssertedKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Paths BELOW the top level that the host read, recorded by <see cref="ExpectationNode"/>
        /// (HPS-46b). <see cref="AssertedKeys"/> proves the top level and stops there: asserting
        /// that <c>items</c> was read says nothing about the thirty-four leaves inside its two rows,
        /// which is the same blind spot one level down.
        /// </summary>
        internal HashSet<string> AssertedPaths { get; } = new(StringComparer.Ordinal);

        internal bool IsAccept => string.Equals(Expect, "accept", StringComparison.Ordinal);

        internal bool IsReject => string.Equals(Expect, "reject", StringComparison.Ordinal);

    }

    /// <summary>Locates the corpus directory, or <c>null</c>.</summary>
    internal static string? FindCorpusDirectory()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < MaxWalkUp && dir is not null; i++)
        {
            string candidate = Path.Combine(dir.FullName, CorpusRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(candidate, "index.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// The manifest version the corpus itself is pinned at. Cross-checked against this host's floor
    /// so the two cannot drift (HPS-31).
    /// </summary>
    internal static int PinnedManifestVersion()
    {
        if (FindCorpusDirectory() is not { } root)
        {
            return 0;
        }

        using JsonDocument index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "index.json")));
        return index.RootElement.TryGetProperty("manifestVersion", out JsonElement version)
            && version.ValueKind == JsonValueKind.Number
                ? version.GetInt32()
                : 0;
    }

    /// <summary>
    /// Loads every case in <paramref name="group"/> that applies to this host.
    /// </summary>
    /// <returns><c>null</c> on success, or the reason the group is unusable.</returns>
    internal static string? LoadGroup(string group, out List<CorpusCase> cases)
    {
        if (FindCorpusDirectory() is not { } root)
        {
            cases = [];
            return $"could not locate {CorpusRelativePath}/index.json by walking up from "
                + $"'{AppContext.BaseDirectory}'. The shared corpus is checked into this repo; a working tree "
                + "without it cannot assert HPS-40 conformance.";
        }

        return LoadGroup(group, root, out cases);
    }

    /// <summary>
    /// Loads every case in <paramref name="group"/> from an explicit corpus root — the seam the
    /// self-test suite uses to point this same reader at the deliberately broken fixtures under
    /// <c>corpus/self-test/</c> (HPS-46).
    /// </summary>
    /// <remarks>
    /// Structural rot in the index is collected rather than fail-fast — a case naming a missing
    /// vector file, a case whose bytes do not parse WITHOUT a <c>malformedJson</c> declaration, a
    /// duplicate id — and any of it comes back as one message listing every problem (HPS-46: the
    /// reader flags rot, it never skips it). <paramref name="cases"/> still receives the cases that
    /// DID load, which is what lets the reader self-test drive its per-case fixtures out of a
    /// deliberately rotten index.
    /// </remarks>
    /// <returns><c>null</c> on success, or the reason the group is unusable.</returns>
    internal static string? LoadGroup(string group, string root, out List<CorpusCase> cases)
    {
        cases = [];

        JsonDocument index;
        try
        {
            index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "index.json")));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return $"corpus index.json under '{root}' is unreadable or not valid JSON: {ex.Message}";
        }

        List<string> problems = [];
        HashSet<string> seenIds = new(StringComparer.Ordinal);

        using (index)
        {
            if (!index.RootElement.TryGetProperty("cases", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return "corpus index.json has no `cases` array";
            }

            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!string.Equals(ReadString(entry, "group"), group, StringComparison.Ordinal))
                {
                    continue;
                }

                // An absent appliesTo means the case binds every host (HPS-41).
                if (entry.TryGetProperty("appliesTo", out JsonElement appliesTo)
                    && appliesTo.ValueKind == JsonValueKind.String
                    && !string.Equals(appliesTo.GetString(), HostKey, StringComparison.Ordinal))
                {
                    continue;
                }

                string id = ReadString(entry, "id");

                // Two cases with one id means one of them is invisible to every id-dispatched suite.
                if (!seenIds.Add(id))
                {
                    problems.Add($"case id '{id}' appears more than once in the index — the later entry is "
                        + "invisible to id-dispatched suites (HPS-46)");
                    continue;
                }

                string file = ReadString(entry, "file");
                string vectorPath = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(vectorPath))
                {
                    problems.Add($"case '{id}' names a missing vector file: {file}");
                    continue;
                }

                string payload = File.ReadAllText(vectorPath);

                // Deliberately-malformed cases declare `malformedJson` in the index and are handed to
                // the parser under test as raw bytes — that is data. Unparseable bytes WITHOUT the
                // declaration are corpus rot the reader must surface (HPS-46), not a fixture.
                if (!ParsesAsJsonObject(payload) && !DeclaresMalformedJson(entry))
                {
                    problems.Add($"case '{id}' file is not valid JSON and the index does not declare "
                        + $"malformedJson — undeclared rot, not a deliberate fixture (HPS-46): {file}");
                    continue;
                }

                cases.Add(new CorpusCase
                {
                    Id = id,
                    Group = group,
                    File = file,
                    Expect = ReadString(entry, "expect"),
                    ErrorContains = ReadString(entry, "errorContains"),
                    Reason = ReadString(entry, "reason"),
                    Payload = payload,
                    Expectations = entry.TryGetProperty("expectations", out JsonElement exp)
                        && exp.ValueKind == JsonValueKind.Object
                            ? exp.Clone()
                            : null,
                });
            }
        }

        if (problems.Count > 0)
        {
            return string.Join('\n', problems);
        }

        if (cases.Count == 0)
        {
            return $"corpus group '{group}' resolved to zero cases for host '{HostKey}' — a suite that asserts "
                + "nothing reports green for the wrong reason (HPS-40)";
        }

        return null;
    }

    /// <summary>
    /// Case files on disk under <paramref name="root"/> that the index's <c>cases</c> never name —
    /// the HPS-46 directory sweep. A vector file the index forgot is invisible to every suite while
    /// looking committed and reviewed.
    /// </summary>
    /// <remarks>
    /// The comparison is against ALL index entries, whatever their group or <c>appliesTo</c> — the
    /// sweep asks "does the index know this file", not "does this host consume it". The index file
    /// itself is skipped, as is any subdirectory carrying its own <c>index.json</c> (a nested
    /// corpus: <c>self-test</c> under the corpus proper, the <c>broken-index-*</c> dirs under
    /// self-test). Paths in <paramref name="files"/> are root-relative with '/' separators.
    /// </remarks>
    /// <returns><c>null</c> on success, or the reason the index could not be read.</returns>
    internal static string? UnindexedCaseFiles(string root, out List<string> files)
    {
        files = [];

        JsonDocument index;
        try
        {
            index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "index.json")));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return $"corpus index.json under '{root}' is unreadable or not valid JSON: {ex.Message}";
        }

        HashSet<string> indexed = new(StringComparer.Ordinal);
        using (index)
        {
            if (index.RootElement.TryGetProperty("cases", out JsonElement entries)
                && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string file = ReadString(entry, "file");
                    if (file.Length > 0)
                    {
                        indexed.Add(file.Replace('\\', '/'));
                    }
                }
            }
        }

        string fullRoot = Path.GetFullPath(root);
        foreach (string path in Directory.EnumerateFiles(fullRoot, "*.json", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(fullRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace('\\', '/');
            if (string.Equals(relative, "index.json", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsUnderNestedCorpus(fullRoot, relative) || indexed.Contains(relative))
            {
                continue;
            }

            files.Add(relative);
        }

        return null;
    }

    /// <summary>Reads an expected string, returning false when the case does not declare the key.</summary>
    internal static bool WantsString(CorpusCase corpusCase, string key, out string value)
    {
        value = string.Empty;
        if (corpusCase.Expectations is not { } expectations
            || !expectations.TryGetProperty(key, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        corpusCase.AssertedKeys.Add(key);
        return true;
    }

    internal static bool WantsBool(CorpusCase corpusCase, string key, out bool value)
    {
        value = false;
        if (corpusCase.Expectations is not { } expectations
            || !expectations.TryGetProperty(key, out JsonElement element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                corpusCase.AssertedKeys.Add(key);
                return true;
            case JsonValueKind.False:
                value = false;
                corpusCase.AssertedKeys.Add(key);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads an expected array as per-element nodes, recording the top-level key. Each element's
    /// own reads record their own paths (HPS-46b).
    /// </summary>
    /// <remarks>
    /// The scalar <c>Wants*</c> accessors cover every <c>manifest</c> expectation, but the vault
    /// listing declares <c>items</c> — an array of per-row objects. Without this the key could never
    /// be asserted, and <see cref="UnassertedExpectations"/> would fail the case for a reason that
    /// is about the reader rather than about the host. Recording the key is where <c>HPS-46</c>
    /// stops and <c>HPS-46b</c> starts: it proves the array was reached, never that anything inside
    /// it was read.
    /// </remarks>
    internal static bool WantsRows(CorpusCase corpusCase, string key, out IReadOnlyList<ExpectationNode> rows)
    {
        rows = [];
        if (corpusCase.Expectations is not { } expectations
            || !expectations.TryGetProperty(key, out JsonElement element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<ExpectationNode> nodes = [];
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            nodes.Add(new ExpectationNode(corpusCase, item, $"{key}[{index}]"));
            index++;
        }

        rows = nodes;
        corpusCase.AssertedKeys.Add(key);
        return true;
    }

    /// <summary>
    /// Reads an expected double. Separate from <see cref="WantsInt"/> because a plan coordinate
    /// truncated to an integer would silently drop the fraction of a foot the case exists to pin.
    /// </summary>
    internal static bool WantsDouble(CorpusCase corpusCase, string key, out double value)
    {
        value = 0.0;
        if (corpusCase.Expectations is not { } expectations
            || !expectations.TryGetProperty(key, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out value))
        {
            return false;
        }

        corpusCase.AssertedKeys.Add(key);
        return true;
    }

    internal static bool WantsInt(CorpusCase corpusCase, string key, out int value)
    {
        value = 0;
        if (corpusCase.Expectations is not { } expectations
            || !expectations.TryGetProperty(key, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        value = (int)element.GetDouble();
        corpusCase.AssertedKeys.Add(key);
        return true;
    }

    /// <summary>
    /// Every <c>expectations</c> key the case declares that the host did not end up asserting, with
    /// the reason. A non-empty result is a failure (HPS-46): either the corpus is asking for an
    /// assertion this host does not make, or it declares one with a type the host cannot read.
    /// </summary>
    /// <remarks>
    /// Checking what was ASSERTED rather than what is merely in an allow-list is what catches the
    /// second case. An allow-list alone says "this host knows the key `orderId`" — it cannot notice
    /// that this particular case spelled its value as a number and so asserted nothing at all.
    /// </remarks>
    internal static IReadOnlyList<string> UnassertedExpectations(
        CorpusCase corpusCase,
        IReadOnlyCollection<string> consumed)
    {
        if (corpusCase.Expectations is not { } expectations)
        {
            return [];
        }

        List<string> problems = [];
        foreach (JsonProperty property in expectations.EnumerateObject())
        {
            if (corpusCase.AssertedKeys.Contains(property.Name))
            {
                continue;
            }

            problems.Add(consumed.Contains(property.Name)
                ? $"'{property.Name}' is declared with an unexpected JSON type ({property.Value.ValueKind}), "
                  + "so this host read nothing from it"
                : $"'{property.Name}' is an expectation this host does not assert. Teach "
                  + "ManifestConformanceTests to consume it — do not delete it from the corpus");
        }

        return problems;
    }

    /// <summary>
    /// Whether a key is prose for a human rather than an assertion for a host — <c>$comment</c>, or
    /// a name ending in <c>Note</c>. The convention holds at every depth (HPS-46).
    /// </summary>
    internal static bool IsDocumentationKey(string key)
        => string.Equals(key, "$comment", StringComparison.Ordinal)
            || key.EndsWith("Note", StringComparison.Ordinal);

    /// <summary>
    /// Every leaf path BELOW the top level of the case's <c>expectations</c> (HPS-46b), in the
    /// <c>items[1].hasManifestVersion</c> form <c>HPS-46a</c> set the precedent for.
    /// </summary>
    /// <remarks>
    /// Three rules the walk encodes. Documentation prunes its whole subtree, because prose is
    /// exempt along with anything under it. An EMPTY container is itself a leaf: <c>formats: []</c>
    /// on the legacy row is the assertion "this row has none", and a walk that found no leaves
    /// under it would let the key go unread for free. An explicit <c>null</c> is a leaf for the
    /// same reason it is a value in a vector file — <c>sha256: null</c> is the row ⛔<c>HPS-27</c>
    /// exists for, and skipping it makes it the one row a suite gets for free.
    /// </remarks>
    internal static IReadOnlyList<string> NestedExpectationPaths(CorpusCase corpusCase)
        => NestedExpectationLeaves(corpusCase).ConvertAll(leaf => leaf.Path);

    private static List<(string Path, JsonValueKind Kind)> NestedExpectationLeaves(CorpusCase corpusCase)
    {
        List<(string Path, JsonValueKind Kind)> leaves = [];
        if (corpusCase.Expectations is { } expectations)
        {
            foreach (JsonProperty property in expectations.EnumerateObject())
            {
                if (IsDocumentationKey(property.Name))
                {
                    continue;
                }

                // The top level is HPS-46's. Only a container with something IN it contributes
                // here — an empty top-level container is a key, and the key is bound one rule up.
                if (HasChildren(property.Value))
                {
                    CollectLeaves(property.Value, property.Name, leaves);
                }
            }
        }

        leaves.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return leaves;
    }

    /// <summary>
    /// Nested expectation paths the host never read. A non-empty result is a failure (HPS-46b).
    /// </summary>
    /// <remarks>
    /// One message, because "not read" is one failure: no assertion covers the path, or it is
    /// declared with a type the accessor rejects, or the assertion path never ran. The declared
    /// JSON type rides along so the wrong-typed cause is diagnosable without being a different
    /// failure.
    /// </remarks>
    internal static IReadOnlyList<string> UnassertedNestedExpectations(CorpusCase corpusCase)
    {
        List<string> problems = [];
        foreach ((string path, JsonValueKind kind) in NestedExpectationLeaves(corpusCase))
        {
            if (corpusCase.AssertedPaths.Contains(path))
            {
                continue;
            }

            problems.Add($"'{path}' is a nested expectation this host never read ({kind}). "
                + "Nothing asserts it, or it is declared with a type the accessor rejects (HPS-46b) — "
                + "teach the suite to consume it, do not delete it from the corpus");
        }

        return problems;
    }

    /// <summary>Whether a value is a container with something inside it.</summary>
    private static bool HasChildren(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(),
        JsonValueKind.Array => element.EnumerateArray().Any(),
        _ => false,
    };

    private static void CollectLeaves(
        JsonElement element,
        string path,
        List<(string Path, JsonValueKind Kind)> leaves)
    {
        if (!HasChildren(element))
        {
            leaves.Add((path, element.ValueKind));
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (IsDocumentationKey(property.Name))
                {
                    continue;
                }

                CollectLeaves(property.Value, $"{path}.{property.Name}", leaves);
            }

            return;
        }

        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            CollectLeaves(item, $"{path}[{index}]", leaves);
            index++;
        }
    }

    /// <summary>
    /// Ids of loaded cases nothing drove. Coverage is asserted mechanically because a reviewer
    /// cannot see a missing branch in a long test (HPS-41).
    /// </summary>
    /// <remarks>
    /// Unused by the <c>manifest</c> suite on purpose — that group drives every case through one
    /// parser in a single loop, so coverage is structural and this could never report anything.
    /// It is here for the groups that dispatch by id, where each case drives a different entry
    /// point and a case added later would otherwise be silently ignored: <c>vault</c> and
    /// <c>auth</c>, which arrive with the next two build-order tranches.
    /// </remarks>
    internal static IReadOnlyList<string> UndrivenCases(
        IEnumerable<CorpusCase> cases,
        IReadOnlySet<string> driven)
    {
        List<string> undriven = [];
        foreach (CorpusCase corpusCase in cases)
        {
            if (!driven.Contains(corpusCase.Id))
            {
                undriven.Add(corpusCase.Id);
            }
        }

        return undriven;
    }

    private static string ReadString(JsonElement element, string field)
        => element.TryGetProperty(field, out JsonElement child) && child.ValueKind == JsonValueKind.String
            ? child.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Whether the vector bytes are a JSON object. Pre-parsing is host-local mechanics — this host
    /// hands <c>Payload</c> to its own parser either way — but WHETHER unparseable bytes are rot is
    /// not, so the answer feeds the undeclared-rot check above (HPS-46).
    /// </summary>
    private static bool ParsesAsJsonObject(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool DeclaresMalformedJson(JsonElement entry)
        => entry.TryGetProperty("malformedJson", out JsonElement declared)
            && declared.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Whether any ancestor directory of <paramref name="relative"/> carries its own
    /// <c>index.json</c> — a nested corpus, which is not this index's to sweep.
    /// </summary>
    private static bool IsUnderNestedCorpus(string root, string relative)
    {
        string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string prefix = string.Empty;
        for (int depth = 0; depth < segments.Length - 1; depth++)
        {
            prefix = prefix.Length == 0 ? segments[depth] : $"{prefix}/{segments[depth]}";
            string candidate = Path.Combine(
                root,
                prefix.Replace('/', Path.DirectorySeparatorChar),
                "index.json");
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
