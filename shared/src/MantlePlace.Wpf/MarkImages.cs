using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MantlePlace.Wpf;

/// <summary>
/// The monogram renders the windows draw, decoded from the assembly's own resources.
/// </summary>
/// <remarks>
/// <para>
/// A manifest resource stream and not a pack URI. A pack URI names an assembly, and AutoCAD loads a
/// plugin into a load context of its own, where WPF's by-name assembly lookup does not find it; a
/// stream asked of this assembly cannot miss, in either host.
/// </para>
/// <para>
/// The files are the monogram renders committed beside the Revit plugin's ribbon imagery, linked into
/// this project at build time (<c>MantlePlace.Wpf.csproj</c>) so no second copy of a binary is
/// committed. Which file to ask for is never decided here: that is <c>MarkRenders</c> in the pure
/// core. Only the mark: no window shows a host's logo, and the host's ribbon glyphs are its own.
/// </para>
/// </remarks>
public static class MarkImages
{
    private const string ResourceStem = "MantlePlace.Wpf.Resources.";

    private static readonly Dictionary<string, ImageSource?> Decoded = new(StringComparer.Ordinal);

    private static readonly object DecodedLock = new();

    /// <summary>The render called <paramref name="fileName"/>, frozen, or null if it is not there or will not decode.</summary>
    /// <remarks>A miss is cached as a miss: a missing render is a build defect, and a retry would not fix it.</remarks>
    public static ImageSource? Decode(string fileName)
    {
        lock (DecodedLock)
        {
            if (Decoded.TryGetValue(fileName, out ImageSource? cached))
            {
                return cached;
            }

            ImageSource? image = null;
            try
            {
                using Stream? stream = typeof(MarkImages).Assembly.GetManifestResourceStream(ResourceStem + fileName);
                if (stream is not null)
                {
                    BitmapImage bitmap = new();
                    bitmap.BeginInit();
                    bitmap.StreamSource = stream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
            }
            catch (NotSupportedException)
            {
                // Not a decodable PNG: the same defect as a missing one.
            }

            Decoded[fileName] = image;
            return image;
        }
    }

    /// <summary>Drops every decoded image. Called from <c>Terminate</c> and nowhere else.</summary>
    public static void Forget()
    {
        lock (DecodedLock)
        {
            Decoded.Clear();
        }
    }
}
