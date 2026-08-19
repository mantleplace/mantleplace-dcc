namespace MantlePlace.Revit.Core;

/// <summary>
/// Resolves a manifest pointer to the entry in the bundle archive that actually carries it.
/// </summary>
/// <remarks>
/// <para>
/// This is not path guessing (HPS-32) — the path always comes from the manifest. What this absorbs
/// is the two ways an archive can spell the same entry: back-slashes from a zip writer that used
/// them, and an enclosing folder, which appears when a user unzips
/// <c>mantleplace_2026-08-09_abcd1234.zip</c> and re-zips the resulting directory. Both are common
/// enough with a hand-downloaded bundle — the permanent local-zip fallback path — that failing on
/// them would read to the user as a corrupt download.
/// </para>
/// <para>
/// An exact match always wins. Failing that, the index looks for a <em>single archive-wide root
/// folder</em> — one first path segment shared by every entry — and retries the pointer beneath it.
/// That is the re-zip case exactly, and nothing else: it will not reach into
/// <c>Backup/Surface/Surface.dxf</c>, because a bundle containing a backup folder has no single
/// root. A looser "any entry ending in <c>/&lt;pointer&gt;</c>" rule would resolve that stale copy and
/// import it silently, which is a worse failure than not finding the file at all.
/// </para>
/// </remarks>
internal sealed class BundleEntryIndex
{
    private readonly Dictionary<string, string> _byNormalisedPath;
    private readonly List<KeyValuePair<string, string>> _entries;
    private readonly string? _singleRoot;

    internal BundleEntryIndex(IEnumerable<string> entryNames)
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

    /// <summary>
    /// Returns the archive's own spelling of the entry for <paramref name="manifestPath"/>, or
    /// <c>null</c> when the bundle does not carry it unambiguously.
    /// </summary>
    internal string? Resolve(string manifestPath)
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

    /// <summary>
    /// The one first path segment every entry shares, or <c>null</c> when the archive has entries
    /// at its root or under more than one top-level folder.
    /// </summary>
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
