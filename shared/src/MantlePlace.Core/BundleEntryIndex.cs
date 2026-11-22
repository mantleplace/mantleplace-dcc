namespace MantlePlace.Core;

public sealed class BundleEntryIndex
{
    private readonly Dictionary<string, string> _byNormalisedPath;
    private readonly List<KeyValuePair<string, string>> _entries;
    private readonly string? _singleRoot;

    public BundleEntryIndex(IEnumerable<string> entryNames)
    {
        _byNormalisedPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _entries = [];

        foreach (string name in entryNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            string normalised = Normalise(name);
            if (normalised.Length == 0 || normalised.EndsWith('/'))
            {
                continue;
            }

            _byNormalisedPath.TryAdd(normalised, name);
            _entries.Add(new KeyValuePair<string, string>(normalised, name));
        }

        _singleRoot = FindSingleRoot();
    }

    public string? Resolve(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return null;
        }

        string normalised = Normalise(manifestPath);
        if (_byNormalisedPath.TryGetValue(normalised, out string? exact))
        {
            return exact;
        }

        if (_singleRoot is null)
        {
            return null;
        }

        return _byNormalisedPath.TryGetValue(_singleRoot + "/" + normalised, out string? rooted)
            ? rooted
            : null;
    }

    private string? FindSingleRoot()
    {
        string? root = null;
        foreach (KeyValuePair<string, string> entry in _entries)
        {
            int slash = entry.Key.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                return null;
            }

            string segment = entry.Key[..slash];
            if (root is null)
            {
                root = segment;
            }
            else if (!string.Equals(root, segment, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return root;
    }

    private static string Normalise(string path)
    {
        string value = path.Replace('\\', '/').Trim();
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.TrimStart('/');
    }
}
