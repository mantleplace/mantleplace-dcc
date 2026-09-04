namespace MantlePlace.Revit.Core;

/// <summary>
/// What the log says about Revit's toposolid smooth shading after the import has tried to turn it
/// on.
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
/// ⚠ <b>Which is exactly why this sentence exists.</b> A plugin that flips a project-wide switch and
/// says nothing is a plugin that gets blamed, three weeks later, for toposolid surface patterns that
/// stopped drawing on somebody else's building. Revit does not draw them while smoothing is on, and
/// it ignores paint and graphic overrides on toposolids too. So the notice names all three of what
/// changed, what it costs, and where to reverse it.
/// </para>
/// <para>
/// The sentence is composed here rather than in the shim for the reason
/// <see cref="SubDivisionDrape"/> was: a curator-visible account of a defect that is invisible from
/// outside Revit has to be asserted by a test rather than by review (<c>HPS-02</c>).
/// </para>
/// </remarks>
public static class TerrainSmoothing
{
    /// <summary>Where a curator turns the setting off again, in Revit's own words.</summary>
    /// <remarks>
    /// Spelled out in every sentence that reports a change, and in the refusal too. A project-wide
    /// display setting the plugin turned on is only acceptable if the curator is told how to undo
    /// it, and a curator reading the refusal is exactly the person who wants to try it by hand.
    /// </remarks>
    public const string RibbonPath = "Massing & Site ▸ Model Site ▸ Toposolid Smooth Shading";

    /// <summary>
    /// The sentence about smooth shading, or <c>null</c> when there is nothing worth saying.
    /// </summary>
    /// <param name="wasEnabled">Whether the document already had smooth shading before this import.</param>
    /// <param name="isEnabled">Whether it has it now — read back from the document, not assumed.</param>
    /// <param name="refusal">Revit's own message, when it refused the setting. <c>null</c> otherwise.</param>
    /// <returns>
    /// A complete sentence, or <c>null</c> when the setting was already on and nothing had to be
    /// done. Null rather than an empty string, for <see cref="SubDivisionDrape"/>'s reason: "there
    /// was nothing to do" and "there was something to do and nothing to report" are different
    /// facts, and only the caller knows whether silence is right.
    /// </returns>
    public static string? Notice(bool wasEnabled, bool isEnabled, string? refusal)
    {
        if (refusal is { Length: > 0 })
        {
            // Revit's words, verbatim, and the manual route in the same breath. The terrain is
            // still there and still correct — only its shading is not — so this is a sentence about
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
}
