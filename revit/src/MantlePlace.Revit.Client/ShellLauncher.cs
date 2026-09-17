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
    /// Every launch goes through here rather than writing its own <c>try</c>, because the two things
    /// worth getting right are both easy to leave out of a copy: the flag above — <c>false</c> is
    /// .NET's default and means "execute this string", not "open it with whatever handles it" — and
    /// the narrow <c>catch</c>, which must not swallow anything but a shell that would not start.
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
    /// Opens a file's folder with that file selected in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A path that is no longer there falls back to its folder rather than being handed to
    /// Explorer.</b> <c>/select</c> does not fail on a missing file — it opens Documents, reports
    /// success, and leaves the curator somewhere they did not ask to be with nothing saying why.
    /// The gap is real rather than theoretical: whatever named this file listed the directory at one
    /// moment and clicks at another, and a log can be deleted in between.
    /// </para>
    /// <para>
    /// ⚠ <c>/select</c> is also the one case <see cref="TryOpen"/> cannot serve, and the one place
    /// the arguments are built as a string rather than a list: Explorer wants the switch and the
    /// path as a single comma-joined, quoted token, and the quotes carry any space in it — a cache
    /// directory is named for a sanitised order id, and a curator's own folder is their business.
    /// </para>
    /// </remarks>
    public static bool TryReveal(string filePath, out string error)
    {
        if (!File.Exists(filePath))
        {
            string folder = Path.GetDirectoryName(filePath) ?? filePath;
            return TryOpenFolder(folder, out error);
        }

        return TryStart(
            new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\""), filePath, out error);
    }
}
