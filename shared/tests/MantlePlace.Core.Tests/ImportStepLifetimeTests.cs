using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

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

        run.Case("copied context buildings are transient — Revit keeps the elements, not the IFC", () =>
            run.Equal(
                ImportStepKinds.LifetimeOf(ImportStepKind.ContextBuildings) == ExtractionLifetime.Transient,
                true,
                "context buildings lifetime"));

        run.Case("the parity layers are transient — Revit builds elements, it stores no path", () =>
        {
            foreach (ImportStepKind kind in (ImportStepKind[])
                [ImportStepKind.RoadCentrelines, ImportStepKind.LandUse, ImportStepKind.LandCover, ImportStepKind.Vegetation])
            {
                run.Equal(
                    ImportStepKinds.LifetimeOf(kind) == ExtractionLifetime.Transient,
                    true,
                    $"{kind} lifetime");
            }
        });

        run.Case("the imagery drape is retained — an appearance asset stores the bitmap's path", () =>
            run.Equal(
                ImportStepKinds.LifetimeOf(ImportStepKind.ImageryDrape) == ExtractionLifetime.Retained,
                true,
                "drape lifetime"));

        run.Case("an unclassified kind defaults to RETAINED, not transient", () =>
        {
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
