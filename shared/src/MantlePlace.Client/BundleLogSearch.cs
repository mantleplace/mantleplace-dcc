using MantlePlace.Core;

namespace MantlePlace.Client;

public static class BundleLogSearch
{
    public static OpenLogsTarget ResolveTarget() => ResolveTarget(BundleCacheLayout.DefaultRoot);

    public static OpenLogsTarget ResolveTarget(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        return OpenLogsTarget.Resolve(
            cacheRoot,
            Directory.Exists(cacheRoot) ? NewestLog(cacheRoot) : null);
    }

    private static string? NewestLog(string cacheRoot)
    {
        string? newest = null;
        DateTime newestWrite = DateTime.MinValue;

        Consider(cacheRoot, ref newest, ref newestWrite);

        foreach (string orderDirectory in TopLevel(cacheRoot, Directory.EnumerateDirectories))
        {
            Consider(orderDirectory, ref newest, ref newestWrite);
        }

        return newest;
    }

    private static void Consider(string directory, ref string? newest, ref DateTime newestWrite)
    {
        foreach (string path in TopLevel(directory, Directory.EnumerateFiles))
        {
            if (!LocalBundleSource.IsLogFileName(Path.GetFileName(path)))
            {
                continue;
            }

            DateTime written;
            try
            {
                written = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (newest is null || written > newestWrite)
            {
                newest = path;
                newestWrite = written;
            }
        }
    }

    private static List<string> TopLevel(string directory, Func<string, string, SearchOption, IEnumerable<string>> list)
    {
        try
        {
            return [.. list(directory, "*", SearchOption.TopDirectoryOnly)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
