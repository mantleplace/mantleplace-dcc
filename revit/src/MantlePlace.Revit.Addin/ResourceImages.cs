using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MantlePlace.Revit.Addin;

internal static class ResourceImages
{
    private const string PackStem = "pack://application:,,,/MantlePlace.Revit.Addin;component/Resources/";

    private static readonly Dictionary<string, ImageSource?> Decoded = new(StringComparer.Ordinal);

    private static readonly object DecodedLock = new();

    internal static ImageSource? Decode(string fileName)
    {
        lock (DecodedLock)
        {
            return DecodeLocked(fileName);
        }
    }

    private static ImageSource? DecodeLocked(string fileName)
    {
        if (Decoded.TryGetValue(fileName, out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            _ = System.Windows.Application.ResourceAssembly;

            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(PackStem + fileName, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (System.IO.IOException)
        {
        }
        catch (UriFormatException)
        {
        }

        Decoded[fileName] = image;
        return image;
    }

    internal static void Forget()
    {
        lock (DecodedLock)
        {
            Decoded.Clear();
        }
    }
}
