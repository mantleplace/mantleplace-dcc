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

    /// <summary>Revit joined two elements and then could not keep them joined. Resolvable by unjoining.</summary>
    CannotKeepElementsJoined,

    /// <summary>A sketch line is fractionally off axis. Unavoidable when the geometry is real ground.</summary>
    InaccurateSketchLine,

    /// <summary>Revit's own IFC importer saying IFC4 is only partially supported.</summary>
    IfcPartiallySupported,
}

/// <summary>What to do with one failure Revit posted.</summary>
public enum ImportFailureAction
{
    /// <summary>Delete it and account for it in the log. Warnings only — Revit refuses on an error.</summary>
    Swallow,

    /// <summary>Apply Revit's own default resolution and say which one. Reserved for an allowlist.</summary>
    Resolve,

    /// <summary>Roll the transaction back, with a stated reason. The default answer to an error.</summary>
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
    /// Decides what happens to one posted failure.
    /// </summary>
    /// <param name="kind">The failure's id, mapped by the shim.</param>
    /// <param name="isError">Whether Revit posted it at error severity or worse.</param>
    /// <param name="hasResolutions">Whether Revit itself offers a way out of it.</param>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Severity decides first and the id can only ever soften an error, never harden a
    /// warning.</b> That asymmetry is the point. The id is not reliable enough to detect a specific
    /// error by — see the composite in the type remarks — so the default for anything at error
    /// severity is <see cref="ImportFailureAction.RollBack"/>, whatever it turns out to be.
    /// </para>
    /// <para>
    /// The one exception is an allowlist of errors Revit offers its own way out of, and it is an
    /// allowlist rather than "any error with a resolution" for a concrete reason: Revit's own
    /// resolution for the toposolid failures is <em>delete the toposolid</em>. Resolving those would
    /// turn a refused import into a silently empty one, which is the failure class
    /// <c>RevitBundleImporter.Execute</c>'s default arm already refuses on principle. Each entry has
    /// to be reasoned about individually before it goes in.
    /// </para>
    /// <para>
    /// An unrecognised WARNING is swallowed, not escalated. An unattended run — the one
    /// <c>MANTLEPLACE_BUNDLE_ZIP</c> exists for — must never leave a modal dialog for nobody to
    /// dismiss, and a warning by definition did not stop Revit doing the work. It is reported
    /// verbatim instead, which is where <c>HPS-21</c>'s "a skip is said, not swallowed" lands here.
    /// </para>
    /// </remarks>
    public static ImportFailureAction Decide(ImportFailureKind kind, bool isError, bool hasResolutions)
    {
        if (!isError)
        {
            return ImportFailureAction.Swallow;
        }

        return hasResolutions && IsResolvable(kind)
            ? ImportFailureAction.Resolve
            : ImportFailureAction.RollBack;
    }

    /// <summary>
    /// The errors this import is willing to let Revit resolve its own way.
    /// </summary>
    /// <remarks>
    /// Currently one. "Can't keep elements joined" arrives from Revit's own IFC importer while it
    /// brings the site model in; its resolution is to unjoin, which is what a curator does by hand
    /// and which loses nothing — the elements stay, they simply stop sharing geometry. Nothing else
    /// has earned a place here.
    /// </remarks>
    private static bool IsResolvable(ImportFailureKind kind)
        => kind == ImportFailureKind.CannotKeepElementsJoined;

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

            ImportFailureKind.InaccurateSketchLine =>
                $"Revit noted {places} where a sketch line sits fractionally off axis. Site boundaries "
                + "follow real ground, so almost none of their edges are square; nothing was moved.",

            ImportFailureKind.IfcPartiallySupported =>
                "Revit's IFC importer reports that IFC4 is only partially supported. The site model "
                + "is IFC4 by design and links as context geometry; what Revit does not read from it "
                + "is detail this import does not rely on.",

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
    /// What to say when Revit resolved an error its own way instead of refusing.
    /// </summary>
    /// <remarks>
    /// The resolution's own caption is quoted rather than described, because it is what the curator
    /// would have clicked had the dialog been shown — and being told which button was pressed on
    /// their behalf is the least this owes them.
    /// </remarks>
    public static string ExplainResolved(ImportFailureKind kind, string resolutionCaption, int count)
    {
        string places = count == 1 ? "1 place" : string.Create(CultureInfo.InvariantCulture, $"{count} places");
        string caption = string.IsNullOrWhiteSpace(resolutionCaption) ? "its default resolution" : $"\"{resolutionCaption.Trim()}\"";

        return kind == ImportFailureKind.CannotKeepElementsJoined
            ? $"Revit could not keep some elements joined in {places} and unjoined them ({caption}). "
                + "Nothing was deleted — the elements simply stop sharing geometry."
            : $"Revit resolved \"{kind}\" in {places} with {caption}.";
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
