using System.Globalization;

namespace MantlePlace.Core;

public static class DrapeAnchor
{
    public static DrapeOffset For(DrapePlacement placement, bool smoothShading, double elementMinXm, double elementMinYm)
    {
        ArgumentNullException.ThrowIfNull(placement);

        return smoothShading
            ? new DrapeOffset(placement.LeftM - elementMinXm, placement.BottomM - elementMinYm)
            : new DrapeOffset(placement.LeftM, placement.BottomM);
    }

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

public readonly record struct DrapeOffset(double Xm, double Ym);
