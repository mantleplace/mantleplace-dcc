namespace MantlePlace.Revit.Core;

/// <summary>
/// What the Bundles panel's Logs button should show: which log to select, and what to say when
/// there is not one yet.
/// </summary>
/// <remarks>
/// <para>
/// There is no single log folder, and that is the whole reason this type exists. Both writers put
/// their file beside the zip they were handed (<see cref="LocalBundleSource.LogPathFor"/>,
/// <see cref="LocalBundleSource.ProbeLogPathFor"/>), so a bundle that came through the vault logs
/// inside its own order folder under the bundle cache, and a zip the curator picked off their
/// desktop logs onto their desktop. Opening one fixed directory is right about half the runs and
/// silently wrong about the other half — the curator stares at a folder that does not contain the
/// run they just watched fail, with nothing saying why.
/// </para>
/// <para>
/// So the rule is: select the most recent log the cache knows about, and where there is none, open
/// the cache and say what is not in it. The alternative — remembering the last path imported — is
/// state this plugin does not keep, and would be wrong the moment a second Revit imported something.
/// </para>
/// </remarks>
public sealed record OpenLogsTarget
{
    private OpenLogsTarget(string folderPath, string? revealFilePath, string? notice)
    {
        FolderPath = folderPath;
        RevealFilePath = revealFilePath;
        Notice = notice;
    }

    /// <summary>The folder to open. Never null — an absent cache is created and opened empty.</summary>
    public string FolderPath { get; }

    /// <summary>The log to select inside <see cref="FolderPath"/>, or <c>null</c> to just open it.</summary>
    public string? RevealFilePath { get; }

    /// <summary>What to tell the curator, or <c>null</c> when the window itself is the answer.</summary>
    public string? Notice { get; }

    /// <summary>
    /// Decides what to show.
    /// </summary>
    /// <param name="cacheRoot">Where vault downloads live, one folder per order.</param>
    /// <param name="newestLogPath">
    /// The most recently written log under <paramref name="cacheRoot"/>, or <c>null</c> if there is
    /// none.
    /// </param>
    public static OpenLogsTarget Resolve(string cacheRoot, string? newestLogPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);

        if (newestLogPath is null)
        {
            return new OpenLogsTarget(
                cacheRoot,
                revealFilePath: null,
                notice: "No import or probe has written a log under this folder yet. A bundle zip you "
                    + "opened from somewhere else logs beside that zip instead, not here.");
        }

        // Its own folder, not the cache root: the curator wants the log, and one folder per order
        // means the root shows them order directories rather than files.
        string folder = Path.GetDirectoryName(newestLogPath) ?? cacheRoot;
        return new OpenLogsTarget(folder, newestLogPath, notice: null);
    }
}
