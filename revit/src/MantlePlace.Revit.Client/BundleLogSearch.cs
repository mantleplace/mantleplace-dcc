using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Finds the most recent import or probe log under the bundle cache, so the Logs button can land the
/// curator on the run they just watched fail rather than on a list of order folders.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the Revit shim because CI cannot build the shim (<c>HPS-02</c>,
/// <c>HPS-42</c>): a directory walk written there would be covered by review alone, and the walk is
/// the half of this button that can be wrong. The shim keeps only the dialog it raises.
/// </para>
/// <para>
/// Newest by last-write time, not by creation time. A log is truncated at the start of a run and
/// appended to throughout it (<c>ImportLog</c>), so the last write is the last thing that happened
/// anywhere on this machine — which is what the curator means by "the log".
/// </para>
/// </remarks>
public static class BundleLogSearch
{
    /// <summary>What the Logs button should show, for this machine's real bundle cache.</summary>
    public static OpenLogsTarget ResolveTarget() => ResolveTarget(BundleCacheLayout.DefaultRoot);

    /// <summary>As <see cref="ResolveTarget()"/>, against an explicit cache root. For tests.</summary>
    public static OpenLogsTarget ResolveTarget(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        // Not-there reads as no-logs rather than as an error: ShellLauncher creates the folder on
        // the way in, so an absent cache and an empty one are the same answer to the curator.
        return OpenLogsTarget.Resolve(
            cacheRoot,
            Directory.Exists(cacheRoot) ? NewestLog(cacheRoot) : null);
    }

    /// <summary>
    /// The newest log under <paramref name="cacheRoot"/>, or <c>null</c> if there is none.
    /// </summary>
    /// <remarks>
    /// Unreadable is treated as absent, throughout. A folder the curator cannot enumerate, a file
    /// deleted between the listing and the stat, a path the cache wrote that is now too long — none
    /// of those should turn a diagnostic button into an error dialog, which is the one thing a
    /// curator reaching for the logs least needs.
    /// </remarks>
    private static string? NewestLog(string cacheRoot)
    {
        string? newest = null;
        DateTime newestWrite = DateTime.MinValue;

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using IEnumerator<string> walk = candidates.GetEnumerator();
        while (true)
        {
            // The enumerator itself throws when it reaches a folder it cannot open, and the throw
            // comes from MoveNext rather than from the call above, so the try has to sit here.
            string path;
            try
            {
                if (!walk.MoveNext())
                {
                    break;
                }

                path = walk.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

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

        return newest;
    }
}
