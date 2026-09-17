namespace MantlePlace.Revit.Core;

/// <summary>
/// Which committed render a slot of a given logical size is handed on a display of a given scale.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A ribbon slot is measured in logical pixels, not device pixels.</b> Revit asks for a 16 px
/// image and a 32 px image and scales whatever it is handed to the display. On a 200% laptop that
/// means the 32 px render is drawn into 64 device pixels — upscaled twice over, which reads as a
/// blurred square rather than as a glyph. Handing over the render that already matches the device is
/// the whole difference, and it costs nothing but choosing a different file name.
/// </para>
/// <para>
/// The rule is one sentence: <b>take the smallest render at least as large as the slot needs, and
/// the largest render there is when none is</b>. Scaling down is cheap and scaling up is not, which
/// is why a display scale between two committed sizes rounds up rather than to the nearest.
/// </para>
/// <para>
/// Pure, and in the core rather than the shim, because it is arithmetic over a list of numbers and
/// the shim is never built in CI (<c>HPS-02</c>, <c>HPS-42</c>). What is left in the shim is turning
/// a file name into a pack URI.
/// </para>
/// </remarks>
public static class RenderSizes
{
    /// <summary>
    /// The render from <paramref name="sizes"/> that <paramref name="slotPixels"/> should be filled
    /// with at <paramref name="displayScale"/>.
    /// </summary>
    /// <param name="sizes">The committed renders, in pixels, smallest first. Never empty.</param>
    /// <param name="slotPixels">The slot's size in logical pixels — 16 or 32 for a ribbon button.</param>
    /// <param name="displayScale">
    /// The display's scale factor: 1.0 at 100%, 1.5 at 150%. A value Windows cannot report — a zero,
    /// a negative, a NaN, or anything below 100%, which Windows does not offer — is read as 100%
    /// rather than refused. This is called while a window is being built, off a visual that may not
    /// be in a tree yet, and a throw there is how a Revit add-in ends the process rather than how it
    /// reports a problem.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="slotPixels"/> is not positive, or <paramref name="sizes"/> is empty. Both are
    /// programming errors in this assembly's own callers rather than anything a display can cause,
    /// so they are refused rather than guessed at.
    /// </exception>
    public static int Pick(IReadOnlyList<int> sizes, int slotPixels, double displayScale)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        if (sizes.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizes), "there is no render to pick");
        }

        if (slotPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotPixels), slotPixels, "a slot has a positive size");
        }

        double scale = double.IsFinite(displayScale) && displayScale > 1.0 ? displayScale : 1.0;

        // The epsilon is for the exact cases, not the near ones: 16 × 1.5 is 24 in binary today and
        // a slot that lands a hair above its own render would otherwise jump a size for nothing.
        double wanted = (slotPixels * scale) - 1e-9;

        for (int i = 0; i < sizes.Count; i++)
        {
            if (sizes[i] >= wanted)
            {
                return sizes[i];
            }
        }

        // Past the largest render. Asking for more cannot conjure one, and the alternative —
        // returning nothing — would leave the button blank on a 300% display.
        return sizes[^1];
    }
}
