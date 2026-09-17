using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Reads the deploy stamp the install script wrote beside the add-in manifest.
/// </summary>
/// <remarks>
/// The one file read behind the About dialog, kept out of the shim so a hosted runner builds and
/// tests it (<c>revit/CLAUDE.md</c>: I/O in <c>Client</c>, never in <c>Addin</c>). Everything that
/// can go wrong reading a file is a <c>null</c> here — the stamp is a courtesy, and About has a
/// sentence for its absence. Parsing is <see cref="InstallStamp.Parse"/>, in Core.
/// </remarks>
public static class InstallStampFile
{
    /// <summary>The file name the deploy script writes, beside the manifest.</summary>
    public const string FileName = "MantlePlace.install.json";

    /// <summary>The stamp in <paramref name="folder"/>, or <c>null</c> when there is none to read.</summary>
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
