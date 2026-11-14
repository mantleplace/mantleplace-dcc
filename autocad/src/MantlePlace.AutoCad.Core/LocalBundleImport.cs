using System.Diagnostics.CodeAnalysis;
using System.IO;
using MantlePlace.Client;

namespace MantlePlace.AutoCad;

/// <summary>
/// Import Bundle (<c>HPS-61</c>): a bundle zip already on disk, and the one check every bundle passes
/// before the importer reads it, whichever way it came.
/// </summary>
/// <remarks>
/// <para>
/// A bundle reaches a drawing two ways: a received export's Import button, and the
/// <see cref="Command"/> command, which takes a zip the curator holds (a download, a copy from a
/// colleague, the bundle cache's copy). Both hand the zip to the shell's one importer, and the importer
/// opens it here, so a file that is not a bundle is refused in the same words whichever way it came,
/// with nothing in the drawing.
/// </para>
/// <para>
/// The command needs no account: it reads the zip and places it, and no request reaches the platform.
/// It also takes its path at the command line (<c>FILEDIA</c> 0, or a script), so it runs with nobody
/// at the screen, as the Revit plugin's local-bundle import does.
/// </para>
/// </remarks>
public static class LocalBundleImport
{
    /// <summary>The registered command, which is what the panel's Import Bundle button types.</summary>
    public const string Command = "MANTLEPLACEIMPORTBUNDLE";

    /// <summary>What a zip with no manifest is told.</summary>
    public const string NotABundle = "This file is not a Mantle Place bundle: it holds no Metadata/manifest.json. Nothing was imported.";

    /// <summary>
    /// Opens <paramref name="zipPath"/> as a bundle, or says why it is not one. Never throws for a bad file.
    /// </summary>
    /// <param name="zipPath">The path as typed or picked; surrounding blanks and one pair of quotes are dropped.</param>
    /// <param name="archive">The opened bundle, which the caller disposes; null when refused.</param>
    /// <param name="refusal">One sentence that ends saying nothing was imported; null when opened.</param>
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

        if (opened.ManifestText is null)
        {
            opened.Dispose();
            refusal = NotABundle;
            return false;
        }

        archive = opened;
        refusal = null;
        return true;
    }

    /// <summary>A path as typed or picked, without surrounding blanks or one pair of surrounding quotes.</summary>
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
