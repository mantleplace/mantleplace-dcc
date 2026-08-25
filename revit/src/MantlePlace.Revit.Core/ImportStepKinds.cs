namespace MantlePlace.Revit.Core;

/// <summary>How long a file extracted for an import step has to survive.</summary>
public enum ExtractionLifetime
{
    /// <summary>
    /// Revit copies the data into the model during import, so the file on disk is done with the
    /// moment the transaction commits. Goes to a scratch directory that is deleted on dispose.
    /// </summary>
    Transient,

    /// <summary>
    /// Revit stores a PATH to this file and re-reads it every time the project is opened — a CAD or
    /// IFC link. It must outlive the import, so it goes to a stable per-order directory that is
    /// never cleaned up automatically. Deleting it would leave the user's links permanently
    /// unresolvable, and Revit gives no warning at link time that this is about to happen.
    /// </summary>
    Retained,
}

/// <summary>Properties of an <see cref="ImportStepKind"/> that the shim must not decide for itself.</summary>
public static class ImportStepKinds
{
    /// <summary>
    /// Whether this kind's extracted file must outlive the import.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from the kind rather than carried on <see cref="ImportStep"/>: the lifetime is a
    /// property of what Revit does with an artifact, identical for every step of the same kind, and
    /// a field would let a caller construct a step whose lifetime contradicts its kind.
    /// </para>
    /// <para>
    /// <b>The default arm is <see cref="ExtractionLifetime.Retained"/> on purpose.</b> A new kind
    /// that nobody classified and that defaulted to Transient would leave a Revit link pointing into
    /// a deleted scratch directory — and the breakage surfaces the next time the project is opened,
    /// on a customer's machine, with no error at link time. Defaulting the other way leaks disk,
    /// which is visible and reversible. Fail safe, not fast, because the fast failure is not here.
    /// </para>
    /// </remarks>
    public static ExtractionLifetime LifetimeOf(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile => ExtractionLifetime.Transient,

        // The TIN is parsed into vertices and handed to Toposolid.Create as points. Revit keeps no
        // reference to the DXF — this is the toposolid path, not the Link CAD path below, which is
        // the same file under the other kind and genuinely is Retained.
        ImportStepKind.ToposurfaceFromSurfaceTin => ExtractionLifetime.Transient,

        // The parity layers become model elements — model curves, subdivisions, direct shapes — and
        // Revit keeps no reference to the file any of them was read out of.
        ImportStepKind.RoadCentrelines => ExtractionLifetime.Transient,
        ImportStepKind.SiteBoundaries => ExtractionLifetime.Transient,
        ImportStepKind.Vegetation => ExtractionLifetime.Transient,

        // Stated rather than left to the default arm below, because this is the first kind whose
        // answer is not obvious from what it builds. The drape becomes a material — geometry-free,
        // and every other geometry-free step so far was Transient — but a Revit appearance asset
        // stores the bitmap's PATH and re-reads it, so a Transient drape would texture correctly for
        // one session and come back unresolved the next time the project is opened.
        ImportStepKind.ImageryDrape => ExtractionLifetime.Retained,

        _ => ExtractionLifetime.Retained,
    };
}
