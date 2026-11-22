namespace MantlePlace.Core;

public sealed record OpenLogsTarget
{
    private OpenLogsTarget(string folderPath, string? revealFilePath, string? notice)
    {
        FolderPath = folderPath;
        RevealFilePath = revealFilePath;
        Notice = notice;
    }

    public string FolderPath { get; }

    public string? RevealFilePath { get; }

    public string? Notice { get; }

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

        string folder = Path.GetDirectoryName(newestLogPath) ?? cacheRoot;
        return new OpenLogsTarget(folder, newestLogPath, notice: null);
    }
}
