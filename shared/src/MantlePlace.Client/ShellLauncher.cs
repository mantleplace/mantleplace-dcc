using System.Diagnostics;

namespace MantlePlace.Client;

public static class ShellLauncher
{
    public static bool TryOpen(string target, out string error)
        => TryOpen(target, Start, out error);

    private static bool TryOpen(string target, Action<ProcessStartInfo> start, out string error)
        => TryStart(start, new ProcessStartInfo(target) { UseShellExecute = true }, target, out error);

    private static void Start(ProcessStartInfo info)
    {
        using Process? started = Process.Start(info);
    }

    private static bool TryStart(Action<ProcessStartInfo> start, ProcessStartInfo info, string target, out string error)
    {
        error = string.Empty;

        try
        {
            start(info);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = target;
            return false;
        }
    }

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

    public static bool TryReveal(string filePath, string folder, out string error)
        => TryReveal(filePath, folder, Start, out error);

    public static bool TryReveal(string filePath, string folder, Action<ProcessStartInfo> start, out string error)
    {
        ArgumentNullException.ThrowIfNull(start);

        if (File.Exists(filePath))
        {
            return TryStart(
                start,
                new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = false },
                filePath,
                out error);
        }

        if (Directory.Exists(folder))
        {
            return TryOpen(folder, start, out error);
        }

        error = filePath;
        return false;
    }
}
