using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MantlePlace.Wpf;

public static class MarkImages
{
    private const string ResourceStem = "MantlePlace.Wpf.Resources.";

    private static readonly Dictionary<string, ImageSource?> Decoded = new(StringComparer.Ordinal);

    private static readonly object DecodedLock = new();

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
            }

            Decoded[fileName] = image;
            return image;
        }
    }

    public static void Forget()
    {
        lock (DecodedLock)
        {
            Decoded.Clear();
        }
    }
}
