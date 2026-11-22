namespace MantlePlace.Core;

public static class TerrainSmoothing
{
    public const string RibbonPath = "Massing & Site ▸ Model Site ▸ Toposolid Smooth Shading";

    public static string? Notice(bool wasEnabled, bool isEnabled, string? refusal)
    {
        if (refusal is { Length: > 0 })
        {
            return "The terrain will shade as flat triangles rather than as a surface: Revit refused "
                + $"the toposolid smooth shading setting — {refusal} You can turn it on by hand under "
                + $"{RibbonPath}.";
        }

        if (!isEnabled)
        {
            return wasEnabled
                ? "⚠ This project's toposolid smooth shading was on before this import and reads as "
                    + "off now, so the ground will shade as flat triangles. This is a plugin defect — "
                    + "please report it with this log."
                : "⚠ Revit accepted the toposolid smooth shading setting and then read it back as off, "
                    + "so the ground will shade as flat triangles. This is a plugin defect — please "
                    + "report it with this log.";
        }

        if (wasEnabled)
        {
            return null;
        }

        return "Turned on Revit's toposolid smooth shading, so the ground shades as a surface rather "
            + "than as flat triangles. Nothing about the terrain's geometry changed — a toposolid is "
            + "a triangulated mesh either way, and this is how Revit draws it. "
            + $"⚠ It is a project-wide display setting ({RibbonPath}): it applies to every toposolid "
            + "in this project, including any you modelled yourself, and while it is on Revit does "
            + "not draw toposolid surface patterns and ignores paint and graphic overrides on them. "
            + "Turn it off there if you need those back.";
    }

    public static bool SettleBeforeCuts(bool drapePlanned, int newSubDivisions)
        => drapePlanned && newSubDivisions > 0;

    public static string DrapeNotice(bool isEnabled)
        => isEnabled
            ? "The aerial photograph is placed for Revit's toposolid smooth shading, which is on. "
                + $"⚠ Leave it on while you need the imagery ({RibbonPath}): under flat shading Revit "
                + "measures a real-world texture from the project origin, under smooth shading from "
                + "each toposolid's own corner, and the photograph is placed for the second — turned "
                + "off, every triangle carries its own slice of it, each slice showing unrelated "
                + "ground, and the site reads as a shattered mosaic. Turn it back on and it reads "
                + "correctly again."
            : "⚠ Revit's toposolid smooth shading is off and this import could not turn it on, so the "
                + "aerial photograph was placed for flat shading: positioned correctly, but every "
                + "triangle carries its own slice of it and the ground reads as a mosaic. If you turn "
                + $"smooth shading on by hand ({RibbonPath}), import this bundle again so the "
                + "photograph is re-placed for it.";
}
