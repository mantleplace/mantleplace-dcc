using System.Globalization;
using System.Text.Json;

namespace MantlePlace.Core.Tests;

internal static class ConformanceCorpus
{
    internal const string HostKey = BundleManifestReader.HostKey;

    private const string CorpusRelativePath = "tools/manifest-conformance/corpus";

    private const int MaxWalkUp = 10;

    internal sealed class CorpusCase
    {
        internal required string Id { get; init; }

        internal required string Group { get; init; }

        internal required string File { get; init; }

        internal required string Expect { get; init; }

        internal string ErrorContains { get; init; } = string.Empty;

        internal string Reason { get; init; } = string.Empty;

        internal string AppliesTo { get; init; } = string.Empty;

        internal required string Payload { get; init; }

        internal JsonElement? Expectations { get; init; }

        internal HashSet<string> AssertedKeys { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> AssertedPaths { get; } = new(StringComparer.Ordinal);

        internal bool IsAccept => string.Equals(Expect, "accept", StringComparison.Ordinal);

        internal bool IsReject => string.Equals(Expect, "reject", StringComparison.Ordinal);

    }

    internal static string? FindCorpusDirectory()
        => RepoTree.Find(CorpusRelativePath, dir => File.Exists(Path.Combine(dir, "index.json")));

    internal static string PinnedManifestVersion()
    {
        if (FindCorpusDirectory() is not { } root)
        {
            return string.Empty;
        }

        using JsonDocument index = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "index.json")));
        if (!index.RootElement.TryGetProperty("manifestVersion", out JsonElement version))
        {
            return string.Empty;
        }

        return version.ValueKind switch
        {
            JsonValueKind.String => version.GetString() ?? string.Empty,
            JsonValueKind.Number => version.GetInt32().ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    internal static string? LoadGroup(string group, out List<CorpusCase> cases)
        => LoadGroupFor(group, HostKey, out cases);

    internal static string? LoadGroupFor(string group, string hostKey, out List<CorpusCase> cases)
    {
        if (FindCorpusDirectory() is not { } root)
        {
            cases = [];
            return $"could not locate {CorpusRelativePath}/index.json by walking up from "
                + $"'{AppContext.BaseDirectory}'. The shared corpus is checked into this repo; a working tree "
                + "without it cannot assert HPS-40 conformance.";
        }

        return LoadGroup(group, root, hostKey, out cases);
    }

    internal static string? LoadGroup(string group, string root, out List<CorpusCase> cases)
        => LoadGroup(group, root, HostKey, out cases);

    internal static string? LoadGroup(string group, string root, string hostKey, out List<CorpusCase> cases)
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

                if (entry.TryGetProperty("appliesTo", out JsonElement appliesTo)
                    && appliesTo.ValueKind == JsonValueKind.String
                    && !string.Equals(appliesTo.GetString(), hostKey, StringComparison.Ordinal))
                {
                    continue;
                }

                string id = ReadString(entry, "id");

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
                    AppliesTo = ReadString(entry, "appliesTo"),
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
            return $"corpus group '{group}' resolved to zero cases for host '{hostKey}' — a suite that asserts "
                + "nothing reports green for the wrong reason (HPS-40)";
        }

        return null;
    }

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

    internal static bool IsDocumentationKey(string key)
        => string.Equals(key, "$comment", StringComparison.Ordinal)
            || key.EndsWith("Note", StringComparison.Ordinal);

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

                if (HasChildren(property.Value))
                {
                    CollectLeaves(property.Value, property.Name, leaves);
                }
            }
        }

        leaves.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return leaves;
    }

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
