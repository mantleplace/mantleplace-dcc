using MantlePlace.Core;

namespace MantlePlace.Client;

public static class InstallStampFile
{
    public const string FileName = "MantlePlace.install.json";

    public static InstallStamp? Read(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        string path = Path.Combine(folder, FileName);
        try
        {
            return File.Exists(path) ? InstallStamp.Parse(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
