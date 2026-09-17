namespace MantlePlace.Revit.Core;

/// <summary>
/// The committed renders of the Mantle Place mark, and which one a slot should be handed.
/// </summary>
/// <remarks>
/// <para>
/// The mark's renders are not produced from anything in this repository: their input is a private
/// vector source and <c>tools/brand-assets</c> is what reads it
/// (<c>docs/adr/0009-host-assets-render-the-monogram.md</c>). What IS in this repository is the list
/// of sizes that came out, and the table in
/// <c>revit/src/MantlePlace.Revit.Addin/Resources/src/README.md</c> that says which display scale
/// takes which. This type is that table, in the one language that can be asserted without Revit.
/// </para>
/// <para>
/// <b>Without this, the 24, 48 and 64 px renders look like dead weight and somebody deletes them.</b>
/// </para>
/// </remarks>
public static class MarkRenders
{
    /// <summary>
    /// The renders that exist in <c>Resources</c>, smallest first.
    /// </summary>
    /// <remarks>
    /// A render added to <c>Resources</c> and not added here would never be chosen; one removed from
    /// <c>Resources</c> and left here would be asked for by name and not be there. Both are what the
    /// headless suite checks, against the files themselves.
    /// </remarks>
    public static IReadOnlyList<int> Sizes { get; } = [16, 24, 32, 48, 64];

    /// <summary>The render size for a slot of <paramref name="slotPixels"/> logical pixels.</summary>
    /// <param name="slotPixels">The slot's size in logical pixels — 16 or 32 for a ribbon button.</param>
    /// <param name="displayScale">The display's scale factor; see <see cref="RenderSizes.Pick"/>.</param>
    public static int SizeFor(int slotPixels, double displayScale)
        => RenderSizes.Pick(Sizes, slotPixels, displayScale);

    /// <summary>The file name of that render, as it is spelled in <c>Resources</c>.</summary>
    public static string FileNameFor(int slotPixels, double displayScale)
        => FileNameOf(SizeFor(slotPixels, displayScale));

    /// <summary>The file name of one render size, without choosing it.</summary>
    public static string FileNameOf(int size) => $"MantlePlaceMark_{size}.png";
}
