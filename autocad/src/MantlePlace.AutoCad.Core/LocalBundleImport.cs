using System.Diagnostics.CodeAnalysis;
using System.IO;
using MantlePlace.Client;

namespace MantlePlace.AutoCad;

public static class LocalBundleImport
{
    public const string Command = "MANTLEPLACEIMPORTBUNDLE";

    public const string NotABundle = "This file is not a Mantle Place bundle: it holds no Manifest.json or Metadata/manifest.json. Nothing was imported.";

    public static bool TryOpen(
        string? zipPath,
        [NotNullWhen(true)] out LocalBundleArchive? archive,
        [NotNullWhen(false)] out string? refusal)
    {
        archive = null;
        string path = Typed(zipPath);
        if (path.Length == 0)
        {
            refusal = "No bundle was named. Nothing was imported.";
            return false;
        }

        if (!File.Exists(path))
        {
            refusal = $"There is no file at {path}. Nothing was imported.";
            return false;
        }

        LocalBundleArchive opened;
        try
        {
            opened = LocalBundleArchive.Open(path);
        }
        catch (InvalidDataException)
        {
            refusal = "This file is not a Mantle Place bundle: it is not a zip archive. Nothing was imported.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refusal = $"The file at {path} could not be read ({ex.Message}). Nothing was imported.";
            return false;
        }

        if (opened.Manifest is null && opened.ManifestText is null)
        {
            opened.Dispose();
            refusal = NotABundle;
            return false;
        }

        archive = opened;
        refusal = null;
        return true;
    }

    public static string Typed(string? text)
    {
        string path = (text ?? string.Empty).Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1].Trim();
        }

        return path;
    }
}
