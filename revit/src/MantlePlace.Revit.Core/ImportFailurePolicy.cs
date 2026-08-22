using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>The Revit failures this import can provoke, named so the policy can be asserted headlessly.</summary>
/// <remarks>
/// One entry per <c>BuiltInFailures</c> id the shim maps. <see cref="Unknown"/> is not a gap — it is
/// the arm that catches every failure this build has never heard of, and it is deliberately
/// reachable rather than an assertion.
/// </remarks>
public enum ImportFailureKind
{
    /// <summary>A failure id this build does not recognise.</summary>
    Unknown = 0,

    /// <summary>The new terrain overlaps a floor the project already contained.</summary>
    ToposolidFloorOverlap,

    /// <summary>The new terrain overlaps another toposolid.</summary>
    ToposolidOverlap,

    /// <summary>A site-boundary subdivision overlaps another.</summary>
    ToposolidSubregionOverlap,

    /// <summary>Two shape-edit points landed on the same spot in plan.</summary>
    SlabShapeVerticesCoincident,

    /// <summary>Revit dropped shape-edit vertices it could not use.</summary>
    SlabShapeVerticesDeleted,

    /// <summary>The terrain is steeper than Revit's own threshold. Real ground can be.</summary>
    ToposolidSlopeExceedsThreshold,

    /// <summary>The shape edit would make the solid thinner than its type allows.</summary>
    SlabShapeTooThin,

    /// <summary>The outer "Slab Shape Edit failed. [Description]" wrapper.</summary>
    SlabShapeEditFailed,
}

/// <summary>What to do with one failure Revit posted.</summary>
public enum ImportFailureAction
{
    /// <summary>Delete it and account for it in the log. Warnings only — Revit refuses on an error.</summary>
    Swallow,

    /// <summary>Roll the transaction back, with a stated reason. The only honest answer to an error.</summary>
    RollBack,
}

/// <summary>
/// Which Revit failures an unattended import may absorb, and what to tell the curator about the
/// ones it absorbed. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Severity decides first, and the id only ever chooses the wording.</b> The failure the first
/// real import hit is a composite: <c>SlabShapeFailures.SlabShapeEditFailedError</c> is
/// <em>"Slab Shape Edit failed. [Description]"</em> and it substitutes
/// <c>SlabShapeFailedTooThin</c>'s <em>"…too thin for its given type."</em> into itself. A policy
/// keyed on the inner id would therefore never fire on the outer one, and the import would go back
/// to a dead-end modal for exactly the case this type exists to handle. Keying on severity cannot
/// miss.
/// </para>
/// <para>
/// The second rule is that nothing is absorbed silently. An import that suppresses a dialog and says
/// nothing has moved the problem rather than solved it, and the curator ends up trusting a model
/// whose provenance no longer matches what they saw. Every swallowed warning is counted and named.
/// </para>
/// </remarks>
public static class ImportFailurePolicy
{
    /// <summary>
    /// Decides what happens to one posted failure, from its severity alone.
    /// </summary>
    /// <param name="isError">Whether Revit posted it at error severity or worse.</param>
    /// <remarks>
    /// <para>
    /// It takes no <see cref="ImportFailureKind"/> on purpose, and that is the whole finding rather
    /// than a simplification: the id is not reliable enough to decide on. See the type remarks for
    /// the composite that proves it.
    /// </para>
    /// <para>
    /// An unrecognised WARNING is therefore swallowed, not escalated. An unattended run — the one
    /// <c>MANTLEPLACE_BUNDLE_ZIP</c> exists for — must never leave a modal dialog for nobody to
    /// dismiss, and a warning by definition did not stop Revit doing the work. It is reported
    /// verbatim instead, which is where <c>HPS-21</c>'s "a skip is said, not swallowed" lands here.
    /// </para>
    /// </remarks>
    public static ImportFailureAction Decide(bool isError)
        => isError ? ImportFailureAction.RollBack : ImportFailureAction.Swallow;

    /// <summary>
    /// One sentence for <paramref name="count"/> swallowed warnings of one kind.
    /// </summary>
    /// <remarks>
    /// These read as explanations, not as apologies. The eight toposolid/floor overlaps the first
    /// real import produced were <em>correct geometry</em> — site context imported into a project
    /// that already contains a building — and today the curator cannot tell them apart from the one
    /// error that actually killed the transaction, because both arrive as pages of the same modal.
    /// </remarks>
    public static string Explain(ImportFailureKind kind, int count)
    {
        string places = count == 1 ? "1 place" : string.Create(
            CultureInfo.InvariantCulture, $"{count} places");

        return kind switch
        {
            ImportFailureKind.ToposolidFloorOverlap =>
                $"The terrain overlaps floors already in this project, in {places}. That is expected "
                + "when you import site context into a project that already contains a building — "
                + "nothing in those floors was changed.",

            ImportFailureKind.ToposolidOverlap =>
                $"The terrain overlaps another toposolid in {places}. If you have imported this area "
                + "before, the older terrain is still there.",

            ImportFailureKind.ToposolidSubregionOverlap =>
                $"Site-boundary subdivisions overlap each other in {places}. The published land-use "
                + "polygons genuinely do overlap; Revit kept them all.",

            ImportFailureKind.SlabShapeVerticesCoincident =>
                $"Revit found {places} where two terrain points share the same position in plan and "
                + "kept one of each. The surface is unchanged.",

            ImportFailureKind.SlabShapeVerticesDeleted =>
                $"Revit dropped terrain points it could not use, in {places}.",

            ImportFailureKind.ToposolidSlopeExceedsThreshold =>
                $"The terrain is steeper than Revit's slope warning threshold in {places}. Real "
                + "ground can be; nothing was flattened.",

            _ => $"Revit raised {places} of \"{kind}\" while building the terrain.",
        };
    }

    /// <summary>
    /// What to say when an error rolled a step back.
    /// </summary>
    /// <remarks>
    /// Revit's own text is quoted rather than paraphrased. It is the only thing a curator can search
    /// for, and paraphrasing it would strand them between our wording and Autodesk's.
    /// </remarks>
    public static string ExplainRollBack(string stepLabel, string revitText)
    {
        string quoted = string.IsNullOrWhiteSpace(revitText) ? "no reason given" : revitText.Trim();
        return $"{stepLabel} was rolled back: Revit refused it with \"{quoted}\". Nothing from this "
            + "step was left in the project.";
    }

    /// <summary>
    /// What to say about a warning kind this build has never seen.
    /// </summary>
    /// <remarks>
    /// The id is included verbatim precisely because it is unrecognised: it is the only thing that
    /// lets the next session add the case without a second Revit run.
    /// </remarks>
    public static string ExplainUnknownWarning(string failureId, string revitText, int count)
    {
        string places = count == 1 ? "once" : string.Create(CultureInfo.InvariantCulture, $"{count} times");
        string quoted = string.IsNullOrWhiteSpace(revitText) ? failureId : revitText.Trim();
        return $"Revit raised a warning this plugin does not recognise, {places}: \"{quoted}\" "
            + $"(id {failureId}). It was allowed through; nothing was rolled back.";
    }
}
