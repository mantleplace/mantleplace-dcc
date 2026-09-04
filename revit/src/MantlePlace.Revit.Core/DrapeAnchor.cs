using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// Where Revit measures a real-world-scaled texture's offset from, and therefore what the drape
/// has to write so the photograph lands on the ground it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>The origin depends on the smooth-shading setting, and that was measured, not read.</b>
/// On a 1,419 × 1,413 m site the same view was exported under both settings and every region
/// matched by free translation against the published photograph:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Flat shading</b> (Toposolid Smooth Shading off): the offset is measured from the
/// <em>project origin</em>, but per face, in each face's own plane. At the scale of the site the
/// photograph is where it should be; at the scale of a triangle every face carries its own slice,
/// displaced by its slope, and the ground reads as a mosaic. That mosaic was the faceting complaint,
/// and no lighting, view style or self-illumination touches it — it is mapping, not shading.
/// </description></item>
/// <item><description>
/// <b>Smooth shading</b>: the offset is measured from the <em>element's bounding-box minimum
/// corner</em>, continuously across the whole surface. Written for the origin, the photograph
/// appears rolled by half its size in both axes, and the repeat seam lands on the origin as four
/// quarters meeting at a cross. Written for the corner, it sits within 1.6 m of the photograph
/// everywhere, on smooth ground.
/// </description></item>
/// </list>
/// <para>
/// So the plugin wants smooth shading, and writes the offset for it. Because the corner is the
/// <em>element's</em>, one material carries one placement: the terrain and every subdivision on it
/// need their own material, each anchored to its own corner. That is the whole reason
/// <see cref="For"/> takes the element's corner rather than the site's.
/// </para>
/// <para>
/// Pure, so the arithmetic and the sentence are asserted headlessly (<c>HPS-02</c>). The shim reads
/// the bounding box and the setting; nothing here knows what a Revit element is.
/// </para>
/// </remarks>
public static class DrapeAnchor
{
    /// <summary>
    /// The <c>texture_RealWorldOffset</c> pair to write for one element, in frame-local metres.
    /// </summary>
    /// <param name="placement">Where the image sits on the ground: its south-west corner is the offset under flat shading.</param>
    /// <param name="smoothShading">Whether the document renders toposolids smooth-shaded.</param>
    /// <param name="elementMinXm">The element's bounding-box minimum easting, frame-local metres.</param>
    /// <param name="elementMinYm">The element's bounding-box minimum northing, frame-local metres.</param>
    public static DrapeOffset For(DrapePlacement placement, bool smoothShading, double elementMinXm, double elementMinYm)
    {
        ArgumentNullException.ThrowIfNull(placement);

        return smoothShading
            ? new DrapeOffset(placement.LeftM - elementMinXm, placement.BottomM - elementMinYm)
            : new DrapeOffset(placement.LeftM, placement.BottomM);
    }

    /// <summary>One log line saying which origin the offset was written for, with the numbers.</summary>
    public static string Describe(
        string element,
        DrapePlacement placement,
        bool smoothShading,
        double elementMinXm,
        double elementMinYm,
        DrapeOffset offset)
    {
        ArgumentNullException.ThrowIfNull(placement);

        return smoothShading
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"drape: {element} anchored for smooth shading — the image's south-west corner is at "
                + $"({placement.LeftM:0.0}, {placement.BottomM:0.0}) m from the origin and the element's corner at "
                + $"({elementMinXm:0.0}, {elementMinYm:0.0}) m, so the offset is written as ({offset.Xm:0.0}, {offset.Ym:0.0}) m.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"drape: {element} anchored for flat shading — the offset is the image's south-west corner, "
                + $"({offset.Xm:0.0}, {offset.Ym:0.0}) m from the origin.");
    }
}

/// <summary>A <c>texture_RealWorldOffsetX/Y</c> pair, in frame-local metres.</summary>
public readonly record struct DrapeOffset(double Xm, double Ym);
