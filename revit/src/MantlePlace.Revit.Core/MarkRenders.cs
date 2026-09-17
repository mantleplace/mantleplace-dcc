namespace MantlePlace.Revit.Core;

/// <summary>
/// Which committed render of the Mantle Place mark belongs in a slot of a given logical size.
/// </summary>
/// <remarks>
/// <para>
/// The renders are <c>MantlePlace.Revit.Addin/Resources/MantlePlaceMark_*.png</c>, produced from the
/// private canonical mark by <c>tools/brand-assets</c> (<c>ADR 0009</c>). This type does not read
/// them; it names one.
/// </para>
/// <para>
/// ⛔ <b>The choice is the display scale's, not the slot's.</b> WPF and the Revit ribbon both measure
/// in logical pixels and scale whatever image they are handed to the device. Hand a 32 px render to a
/// 32 px slot on a 200% laptop and it is drawn at 64 device pixels from 32 — a blurred square where
/// the mark should be. The table this implements is
/// <c>Resources/src/README.md ▸ Which mark file for which display scale</c>, and that README exists
/// because without it the 24, 48 and 64 px renders look like dead weight and someone deletes them.
/// </para>
/// </remarks>
public static class MarkRenders
{
    /// <summary>
    /// The renders that exist, smallest first.
    /// </summary>
    /// <remarks>
    /// Kept in step with the folder by hand, and asserted as a literal in the suite: a size listed
    /// here that is not on disk is a name this type will hand out for a file nobody can open.
    /// </remarks>
    public static IReadOnlyList<int> Sizes { get; } = [16, 24, 32, 48, 64];

    /// <summary>The slot a window header gives the mark, in logical pixels.</summary>
    public const int HeaderSlotPixels = 32;

    /// <summary>
    /// The render for a <paramref name="logicalPixels"/> slot at <paramref name="displayScale"/>.
    /// </summary>
    /// <remarks>
    /// The smallest render that covers the device size, so nothing is ever scaled up; a scale between
    /// two steps takes the next render up and lets the host scale down, which is the direction that
    /// costs least. Past the largest render the largest render is the answer — asking for more cannot
    /// conjure one, and the alternative is a blank header.
    /// </remarks>
    /// <param name="logicalPixels">The slot's size in logical pixels. Must be positive.</param>
    /// <param name="displayScale">
    /// Windows' scale factor — <c>1.0</c> at 100%. <b>Anything below 1.0 is read as 1.0</b>, NaN
    /// included: Windows offers no sub-100% scale, so a value there is a <c>DpiScaleX</c> read off a
    /// visual that is not in a tree yet rather than a display anyone has. Guessing 100% is the failure
    /// that is merely blurry, not the one that throws on a dispatcher.
    /// </param>
    public static int SizeFor(int logicalPixels, double displayScale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalPixels);

        double scale = double.IsNaN(displayScale) || displayScale < 1.0 ? 1.0 : displayScale;

        // The epsilon absorbs a scale that arrives a hair above an exact step. Every scale Windows
        // offers is a power-of-two fraction of 96 dpi and multiplies out exactly — 32 × 1.5 really is
        // 48.0 — but DpiScaleX is a computed double off a monitor's reported dpi, and a 1.5 that
        // arrives as 1.4999999999999998 or 1.5000000000000002 would otherwise decide between the
        // 48 px render and the 64 px one on the last bit of a float.
        int devicePixels = (int)Math.Ceiling((logicalPixels * scale) - 0.001);

        foreach (int size in Sizes)
        {
            if (size >= devicePixels)
            {
                return size;
            }
        }

        return Sizes[^1];
    }

    /// <summary>The file name of the render <see cref="SizeFor"/> picks, as it sits in <c>Resources</c>.</summary>
    /// <param name="logicalPixels">The slot's size in logical pixels. Must be positive.</param>
    /// <param name="displayScale">Windows' scale factor — <c>1.0</c> at 100%.</param>
    public static string FileNameFor(int logicalPixels, double displayScale)
        => $"MantlePlaceMark_{SizeFor(logicalPixels, displayScale)}.png";
}
