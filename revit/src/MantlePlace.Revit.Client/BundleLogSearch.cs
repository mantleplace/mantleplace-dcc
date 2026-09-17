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
    /// <para>
    /// ⛔ <b>Two shallow passes, never <c>SearchOption.AllDirectories</c>.</b> A log is always beside
    /// the zip it describes, which is one level down — <c>&lt;order&gt;/bundle.zip…log</c> — while
    /// the sibling <see cref="BundleCacheLayout.ExtractedDirectoryName"/> holds every tile, raster
    /// and mesh of every bundle ever imported, and <c>HPS-44</c> means nothing there is ever evicted.
    /// A recursive walk would therefore stat tens of thousands of files that cannot be logs, on
    /// Revit's UI thread, before the button that exists for "something went wrong" shows anything at
    /// all. Depth is the whole optimisation: this visits exactly the directories a log can be in.
    /// </para>
    /// <para>
    /// Unreadable is treated as absent, throughout — and per directory, so one folder the curator
    /// cannot enumerate costs its own logs rather than everybody's. A file deleted between the
    /// listing and the stat, a path the cache wrote that is now too long: none of those should turn
    /// a diagnostic button into an error dialog, which is the one thing a curator reaching for the
    /// logs least needs.
    /// </para>
    /// </remarks>
    private static string? NewestLog(string cacheRoot)
    {
        string? newest = null;
        DateTime newestWrite = DateTime.MinValue;

        // The root itself first: a zip that names no order still gets a folder of its own, and a
        // future layout that wrote a log beside the roots would otherwise go unseen.
        Consider(cacheRoot, ref newest, ref newestWrite);

        foreach (string orderDirectory in TopLevel(cacheRoot, Directory.EnumerateDirectories))
        {
            Consider(orderDirectory, ref newest, ref newestWrite);
        }

        return newest;
    }

    /// <summary>Takes the newest log directly inside <paramref name="directory"/> into account.</summary>
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

    /// <summary>
    /// One directory's immediate children, as a list, or an empty one where it cannot be read.
    /// </summary>
    /// <remarks>
    /// Materialised rather than streamed because the throw an enumerator raises arrives from
    /// <c>MoveNext</c> rather than from the call that built it, so a lazy sequence puts the
    /// <c>try</c> on every caller's loop and gets forgotten on the next one written.
    /// </remarks>
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
