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
    {
        error = string.Empty;

        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
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
}
