using System.Security.Cryptography;

namespace MantlePlace.Client;

public static class FamilyFileStore
{
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MantlePlace",
        "families");

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
