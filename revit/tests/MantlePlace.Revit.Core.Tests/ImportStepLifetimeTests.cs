using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The Transient/Retained choice is a planner output, not a shim branch — these are the assertions
/// the headless suite could not make while that switch lived in <c>RevitBundleImporter</c>.
/// </summary>
internal static class ImportStepLifetimeTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("a points file is transient — Revit copies it into the model", () =>
            run.Equal(
                ImportStepKinds.LifetimeOf(ImportStepKind.ToposurfaceFromPointsFile) == ExtractionLifetime.Transient,
                true,
                "points file lifetime"));

        run.Case("a linked DXF is retained — the link stores the path", () =>
            run.Equal(
                ImportStepKinds.LifetimeOf(ImportStepKind.ToposurfaceFromSurfaceDxf) == ExtractionLifetime.Retained,
                true,
                "surface DXF lifetime"));

        run.Case("a linked IFC is retained", () =>
            run.Equal(
                ImportStepKinds.LifetimeOf(ImportStepKind.LinkSiteIfc) == ExtractionLifetime.Retained,
                true,
                "IFC lifetime"));

        run.Case("the parity layers are transient — Revit builds elements, it stores no path", () =>
        {
            foreach (ImportStepKind kind in (ImportStepKind[])
                [ImportStepKind.RoadCentrelines, ImportStepKind.SiteBoundaries, ImportStepKind.Vegetation])
            {
                run.Equal(
                    ImportStepKinds.LifetimeOf(kind) == ExtractionLifetime.Transient,
                    true,
                    $"{kind} lifetime");
            }
        });

        run.Case("the imagery drape is retained — an appearance asset stores the bitmap's path", () =>
            // It builds no geometry, which is what every Transient kind above has in common, so the
            // right answer here is the opposite of the one the shape of the step suggests. A
            // Transient drape textures correctly once and comes back unresolved.
            run.Equal(
                ImportStepKinds.LifetimeOf(ImportStepKind.ImageryDrape) == ExtractionLifetime.Retained,
                true,
                "drape lifetime"));

        run.Case("an unclassified kind defaults to RETAINED, not transient", () =>
        {
            // The asymmetry is the whole point. A new step kind that nobody classified and that
            // defaults to Transient leaves a link pointing into a deleted scratch directory — and
            // the breakage only surfaces the next time the project is opened, on the user's
            // machine. Defaulting to Retained leaks disk instead, which is visible and harmless.
            ImportStepKind unclassified = (ImportStepKind)9999;
            run.Equal(
                ImportStepKinds.LifetimeOf(unclassified) == ExtractionLifetime.Retained,
                true,
                "the safe default");
        });

        run.Case("every declared kind resolves without throwing", () =>
        {
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                ExtractionLifetime lifetime = ImportStepKinds.LifetimeOf(kind);
                run.True(
                    lifetime is ExtractionLifetime.Transient or ExtractionLifetime.Retained,
                    $"{kind} resolves to a real lifetime");
            }
        });

        return run.Report("import step lifetime");
    }
}
