using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Decodes one of this assembly's committed renders, by file name, once.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The images are embedded resources, not files beside the DLL.</b> The build action in
/// <c>MantlePlace.Revit.Addin.csproj</c> is <c>Resource</c> and not <c>EmbeddedResource</c>, because
/// a pack URI resolves against the WPF resource table and <c>EmbeddedResource</c> does not populate
/// it. Baking them in is what keeps <c>Package-MantlePlaceRevit.ps1</c> free of a copy step, and
/// what stops a release whose ribbon is blank because one folder did not get zipped.
/// </para>
/// <para>
/// Its own type, apart from <see cref="RibbonImagery"/>, for two reasons. Both the ribbon and the
/// windows' <see cref="BrandChrome"/> decode from this table, and a second copy of the method below
/// would be a second place to forget the <c>ResourceAssembly</c> touch. And <b>nothing here
/// references the Revit API</b>, which is what lets the windows be built and rendered offscreen from
/// the compiled assembly on a machine with no Revit running — the only check this shim's WPF gets,
/// since CI never builds it (<c>HPS-42</c>).
/// </para>
/// <para>
/// Which file name to ask for is never decided here: that is <c>MarkRenders</c> and
/// <c>RibbonGlyphs</c> in the pure core.
/// </para>
/// </remarks>
internal static class ResourceImages
{
    /// <summary>The assembly's own resource table, as a pack URI stem.</summary>
    private const string PackStem = "pack://application:,,,/MantlePlace.Revit.Addin;component/Resources/";

    private static readonly Dictionary<string, ImageSource?> Decoded = new(StringComparer.Ordinal);

    /// <summary>
    /// The render called <paramref name="fileName"/>, frozen, or null if it will not decode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OnLoad</c> and then <c>Freeze</c>: the stream is closed before this returns and the result
    /// is usable from any thread, which matters because a frozen <see cref="ImageSource"/> is the
    /// only kind a ribbon item will hold without complaint.
    /// </para>
    /// <para>
    /// A miss is cached as a miss. A render this assembly asks for and does not find is a defect the
    /// headless suite already fails on, so the remedy is a fixed build rather than a retry on every
    /// theme change — and, for a window header, on every move between monitors.
    /// </para>
    /// </remarks>
    internal static ImageSource? Decode(string fileName)
    {
        if (Decoded.TryGetValue(fileName, out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            // ⚠ Registers the "pack" URI scheme if nothing has yet, and this is the line that does
            // it: touching System.IO.Packaging.PackUriHelper is NOT enough — WPF's own registration
            // runs in Application's static constructor, and without it every pack URI here fails
            // with "The URI prefix is not recognized". Revit hosts WPF, so in practice the scheme is
            // already registered by the time OnStartup runs; this costs a null property read and
            // removes the dependency on that being true.
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
            // Fully qualified: RevitAPI.dll puts an inaccessible IOException in the global namespace,
            // and an unqualified one here resolves to that rather than to the framework's.
            // The resource is not in the table, or is not a decodable PNG. Either is a build defect.
        }
        catch (UriFormatException)
        {
        }

        Decoded[fileName] = image;
        return image;
    }

    /// <summary>Drops every decoded image. Called from <c>OnShutdown</c> and nowhere else.</summary>
    internal static void Forget() => Decoded.Clear();
}
