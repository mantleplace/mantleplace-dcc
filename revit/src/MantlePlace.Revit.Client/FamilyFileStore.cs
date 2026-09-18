using System.Security.Cryptography;

namespace MantlePlace.Revit.Client;

/// <summary>
/// Puts a family the add-in carries inside its assembly onto disk, where <c>Document.LoadFamily</c>
/// can read it.
/// </summary>
/// <remarks>
/// <para>
/// The file is named for the family, because Revit names a loaded family after its file, and it
/// lives in a folder per build, so a newer add-in never loads a file an older one left behind. The
/// older builds' folders are removed on the way past, best effort: a copy another running Revit is
/// reading is left for next time.
/// </para>
/// <para>
/// ⛔ Written as <c>HPS-26</c> writes a download — to <c>.part</c>, verified, then renamed — and a file
/// already there is trusted only when its digest is the one this build carries. Otherwise a copy a
/// crash truncated would be handed to Revit on every import after it, and read as a family that
/// "could not be loaded" with nothing on disk to say why.
/// </para>
/// </remarks>
public static class FamilyFileStore
{
    /// <summary>Where the add-in keeps its families, beside the bundle cache.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        "families");

    /// <summary>Writes <paramref name="content"/> as <paramref name="fileName"/> for this build.</summary>
    /// <param name="build">Identifies the assembly carrying the family; any stable per-build token.</param>
    /// <returns>The file's path.</returns>
    public static string Materialise(string root, string build, string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string directory = Path.Combine(root, build);
        string path = Path.Combine(directory, fileName);
        byte[] wanted = SHA256.HashData(content);

        if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(wanted))
        {
            Directory.CreateDirectory(directory);
            string partial = path + ".part";
            File.WriteAllBytes(partial, content);
            if (!SHA256.HashData(File.ReadAllBytes(partial)).AsSpan().SequenceEqual(wanted))
            {
                File.Delete(partial);
                throw new IOException($"The family written to \"{partial}\" did not read back as written.");
            }

            File.Move(partial, path, overwrite: true);
        }

        RemoveOtherBuilds(root, build);
        return path;
    }

    private static void RemoveOtherBuilds(string root, string build)
    {
        foreach (string other in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(other), build, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(other, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
