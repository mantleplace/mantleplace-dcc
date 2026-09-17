using System.Diagnostics;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Handing something to Windows to open — a URL in the default browser, a folder in Explorer.
/// </summary>
/// <remarks>
/// <para>
/// In <c>Client</c> rather than in the Revit shim because it is I/O and nothing about it is Revit:
/// the shim is the one layer CI never builds, so code that lands there is covered by review alone
/// however simple it looks (<c>revit/CLAUDE.md</c> ▸ "Put I/O in Client, not in the shim"). The shim
/// keeps what is genuinely its own — raising the <c>TaskDialog</c> when this says no.
/// </para>
/// <para>
/// It is also the third caller that made a helper worth having. The sign-in has opened the system
/// browser this way since <c>HPS-05</c>; the Account dropdown now opens the website and the bundle
/// folder the same way, and three copies of one <c>ProcessStartInfo</c> is three places for the
/// <c>UseShellExecute</c> flag to be forgotten — where <c>false</c> is .NET's default and means
/// "execute this string", not "open it with whatever handles it".
/// </para>
/// </remarks>
public static class ShellLauncher
{
    /// <summary>
    /// Opens <paramref name="target"/> with whatever Windows associates with it.
    /// </summary>
    /// <param name="target">A URL or a path.</param>
    /// <param name="error">Empty on success; on failure, a sentence naming the target.</param>
    /// <returns>Whether Windows took it.</returns>
    public static bool TryOpen(string target, out string error)
        => TryStart(new ProcessStartInfo(target) { UseShellExecute = true }, target, out error);

    /// <summary>
    /// The one <c>Process.Start</c> in this assembly, and the one <c>catch</c> around it.
    /// </summary>
    /// <remarks>
    /// Every launch goes through here rather than writing its own <c>try</c>, so the narrow
    /// <c>catch</c> — which must not swallow anything but a shell that would not start — exists
    /// once. It does <b>not</b> centralise <c>UseShellExecute</c>: each caller still builds its own
    /// <see cref="ProcessStartInfo"/> and has to set the flag deliberately, because the two callers
    /// genuinely want opposite values of it.
    /// </remarks>
    private static bool TryStart(ProcessStartInfo start, string target, out string error)
    {
        error = string.Empty;

        try
        {
            using Process? started = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = target;
            return false;
        }
    }

    /// <summary>
    /// Opens a folder, creating it first when it is not there yet.
    /// </summary>
    /// <remarks>
    /// The creation is the point. Explorer's answer to a path that does not exist is a dialog naming
    /// a directory the curator has never heard of, and it does not say the one thing an absent
    /// bundle cache actually means — that no import has run yet. An empty window says that.
    /// </remarks>
    public static bool TryOpenFolder(string folder, out string error)
    {
        error = string.Empty;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = folder;
            return false;
        }

        return TryOpen(folder, out error);
    }

    /// <summary>
    /// Opens <paramref name="folder"/> with <paramref name="filePath"/> selected in it.
    /// </summary>
    /// <param name="filePath">The file to select.</param>
    /// <param name="folder">
    /// The folder to fall back to when the file is gone. The caller passes it rather than letting
    /// this derive it, so the folder a curator is shown is the one the caller decided on.
    /// </param>
    /// <param name="error">Empty on success; on failure, the path nothing could be done with.</param>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A file that is no longer there is never handed to Explorer.</b> <c>/select</c> does not
    /// fail on a missing path — it opens Documents, reports success, and leaves the curator
    /// somewhere they did not ask to be with nothing saying why. The gap is real rather than
    /// theoretical: whatever named this file listed its directory at one moment and the curator
    /// clicks at another.
    /// </para>
    /// <para>
    /// ⛔ <b>And the fallback does not create anything.</b> <see cref="TryOpenFolder"/> would, which
    /// is right when the bundle cache has simply never existed and wrong here: a curator who deleted
    /// that order's folder to reclaim disk would get it silently resurrected, empty, with the
    /// command reporting success over a log that is gone. A folder that is not there fails instead,
    /// and the caller says so.
    /// </para>
    /// <para>
    /// ⚠ <c>/select</c> is also the one case <see cref="TryOpen"/> cannot serve, and the one place
    /// the arguments are built as a string rather than a list: Explorer wants the switch and the
    /// path as a single comma-joined, quoted token, and the quotes carry any space in it — a cache
    /// directory is named for a sanitised order id, and a curator's own folder is their business.
    /// <c>UseShellExecute</c> stays <c>false</c> here, unlike every other launch in this class,
    /// because this one really does mean "run this executable with these arguments".
    /// </para>
    /// </remarks>
    public static bool TryReveal(string filePath, string folder, out string error)
    {
        if (File.Exists(filePath))
        {
            return TryStart(
                new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false },
                filePath,
                out error);
        }

        if (Directory.Exists(folder))
        {
            return TryOpen(folder, out error);
        }

        error = filePath;
        return false;
    }
}
