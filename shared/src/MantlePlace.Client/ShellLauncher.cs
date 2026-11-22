using System.Diagnostics;

namespace MantlePlace.Client;

public static class ShellLauncher
{
    public static bool TryOpen(string target, out string error)
        => TryStart(new ProcessStartInfo(target) { UseShellExecute = true }, target, out error);

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
