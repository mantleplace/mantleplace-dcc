namespace MantlePlace.Revit.Core;

/// <summary>
/// What the log says about Revit's toposolid smooth shading — including the reason it is usually
/// left alone.
/// </summary>
/// <remarks>
/// <para>
/// The setting is a <em>display</em> one and it is <b>document-wide</b>. That is not inferred from
/// Autodesk's ribbon copy: <c>Toposolid.SetSmoothedSurface(Document, bool)</c> and
/// <c>Toposolid.IsSmoothedSurfaceEnabled(Document)</c> are <b>static</b> methods whose only argument
/// is the document — there is no element parameter to pass, so there is no per-toposolid setting to
/// have. One call covers the terrain, every site-boundary subdivision, and every toposolid the
/// curator modelled themselves.
/// </para>
/// <para>
/// ⛔ <b>It cannot be used on a terrain wearing an aerial photograph, and that was measured rather
/// than reasoned.</b> Autodesk documents that smoothing stops toposolid surface patterns drawing and
/// makes Revit ignore paint and graphic overrides. It does one more thing they do not document: it
/// breaks the mapping of a real-world-scaled bitmap. On a 1,419 × 1,413 m site the photograph
/// rendered as four quadrants meeting at a hard cross, each showing different ground, from the same
/// material and the same four texture properties that render correctly with smoothing off. So the
/// choice is not "smooth or faceted". It is "smooth ground under a scrambled photograph, or faceted
/// ground under a correct one", and a site model exists to be accurate before it is pretty.
/// </para>
/// <para>
/// Hence two sentences rather than one. The import enables smoothing only where there is no
/// photograph to lose, and where there is one it says so — including the case that matters most, a
/// curator who already has smoothing on and is about to wonder why their imagery arrived in pieces.
/// </para>
/// <para>
/// Composed here rather than in the shim for the reason <see cref="SubDivisionDrape"/> was: a
/// curator-visible account of a defect that is invisible from outside Revit has to be asserted by a
/// test rather than by review (<c>HPS-02</c>).
/// </para>
/// </remarks>
public static class TerrainSmoothing
{
    /// <summary>Where a curator turns the setting on or off, in Revit's own words.</summary>
    /// <remarks>
    /// Spelled out in every sentence this type produces. A project-wide display setting the plugin
    /// turned on is only acceptable if the curator is told how to undo it — and a curator whose
    /// photograph has arrived in quarters needs the same path to put it right.
    /// </remarks>
    public const string RibbonPath = "Massing & Site ▸ Model Site ▸ Toposolid Smooth Shading";

    /// <summary>
    /// The sentence for a terrain with <b>no</b> photograph on it, where smoothing is a plain win.
    /// </summary>
    /// <param name="wasEnabled">Whether the document already had smooth shading before this import.</param>
    /// <param name="isEnabled">Whether it has it now — read back from the document, not assumed.</param>
    /// <param name="refusal">Revit's own message, when it refused the setting. <c>null</c> otherwise.</param>
    /// <returns>
    /// A complete sentence, or <c>null</c> when the setting was already on and nothing had to be
    /// done. Null rather than an empty string, for <see cref="SubDivisionDrape"/>'s reason: "there
    /// was nothing to do" and "there was something to do and nothing to report" are different facts,
    /// and only the caller knows whether silence is right.
    /// </returns>
    public static string? Notice(bool wasEnabled, bool isEnabled, string? refusal)
    {
        if (refusal is { Length: > 0 })
        {
            // Revit's words, verbatim, and the manual route in the same breath. The terrain is still
            // there and still correct — only its shading is not — so this is a sentence about
            // appearance, not a failed import.
            return "The terrain will shade as flat triangles rather than as a surface: Revit refused "
                + $"the toposolid smooth shading setting — {refusal} You can turn it on by hand under "
                + $"{RibbonPath}.";
        }

        if (!isEnabled)
        {
            // ⛔ Set, and read back off. This is the shape of defect the drape's texture distances
            // had — a call that returns without complaint while the document holds something else —
            // and the only reason it is ever visible is that the value is read back instead of
            // trusted.
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

    /// <summary>
    /// The sentence for a terrain that <b>is</b> wearing the aerial photograph, where smoothing and
    /// the drape cannot both be had.
    /// </summary>
    /// <param name="isEnabled">
    /// Whether the project currently has smooth shading on. <c>true</c> is the loud case: the
    /// photograph this import just laid will not render correctly until it is turned off, and the
    /// curator is the only one who can decide to do that.
    /// </param>
    /// <returns>A complete sentence. Never null — both states are worth a line here.</returns>
    /// <remarks>
    /// ⛔ The plugin does not turn the setting off on the curator's behalf, even in the loud case.
    /// It is project-wide, they may have set it deliberately for toposolids that have nothing to do
    /// with this import, and silently reversing someone's display setting is the same trespass as
    /// silently setting it. Naming exactly what is wrong and exactly where the switch is leaves the
    /// decision where it belongs and still ends the mystery.
    /// </remarks>
    public static string DrapeNotice(bool isEnabled)
        => isEnabled
            ? "⚠ This project has Revit's toposolid smooth shading ON, and the aerial photograph will "
                + "not render correctly while it is. Smoothing breaks the mapping of a real-world-scaled "
                + "image: the photograph appears as four quarters meeting at a cross, each showing the "
                + $"wrong ground. Turn it off under {RibbonPath} and the imagery reads correctly again. "
                + "The plugin has not changed the setting for you, because it is project-wide and "
                + "affects every toposolid you own."
            : "The ground shades as flat triangles rather than as a smooth surface. Revit's toposolid "
                + "smooth shading is what fixes that, and this import deliberately left it off: it "
                + "breaks the mapping of the aerial photograph, which then renders as four quarters "
                + "meeting at a cross, each showing the wrong ground. If you would rather have smooth "
                + $"ground than correct imagery, the setting is under {RibbonPath}.";
}
