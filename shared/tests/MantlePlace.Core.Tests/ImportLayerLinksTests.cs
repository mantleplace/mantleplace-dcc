namespace MantlePlace.Core.Tests;

internal static class ImportLayerLinksTests
{
    private static readonly ImportStepKind[] NotLayers =
    [
        ImportStepKind.SetSharedCoordinates,
        ImportStepKind.SetSiteLocation,
        ImportStepKind.AttributionAndProvenance,
        ImportStepKind.SiteContextView,
    ];

    private static readonly ImportStepKind[] NeverTimed = [ImportStepKind.ToposurfaceFromSurfaceDxf];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("every layer is built by some step kind the planner can emit", () =>
        {
            foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
            {
                run.True(KindsOf(layer).Any(), $"{layer} has no step kind: add its arm to ImportLayers.Of");
            }
        });

        run.Case("every layer has its own window label, not the enum's name falling through", () =>
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
            {
                string? label = WindowLabels.KnownLayerName(layer);
                run.True(!string.IsNullOrWhiteSpace(label), $"{layer} has no window label: add its arm to WindowLabels.KnownLayerName");
                if (label is not null)
                {
                    run.True(seen.Add(label), $"{layer}'s label \"{label}\" is another layer's too");
                }
            }
        });

        run.Case("every layer has a measured row on at least one of its step kinds", () =>
        {
            foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
            {
                run.True(
                    KindsOf(layer).Any(kind => SlowStepNotice.Measured(kind) is not null),
                    $"{layer} has no SlowStepNotice.Measured row: time it and add the row");
            }
        });

        run.Case("reverse: every step kind is a layer's, or is named as no layer on purpose", () =>
        {
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                bool isLayer = ImportLayers.Of(kind) is not null;
                bool declared = NotLayers.Contains(kind);
                run.True(
                    isLayer || declared,
                    $"{kind} belongs to no ImportLayer: map it in ImportLayers.Of, or add it to NotLayers with its reason");
                run.False(
                    isLayer && declared,
                    $"{kind} is mapped to a layer and also listed in NotLayers: take it off the list");
            }
        });

        run.Case("reverse: every step kind has a measured row, bar the one never timed", () =>
        {
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                run.True(
                    SlowStepNotice.Measured(kind) is not null || NeverTimed.Contains(kind),
                    $"{kind} ({LayerOrNone(kind)}) has no SlowStepNotice.Measured row: time it and add the row");
            }
        });

        run.Case("reverse: every step kind that is no layer still has a curator's name of its own", () =>
        {
            foreach (ImportStepKind kind in NotLayers)
            {
                run.False(
                    string.Equals(WindowLabels.StepName(kind), kind.ToString(), StringComparison.Ordinal),
                    $"{kind} has no name in WindowLabels.StepName: add its arm");
            }
        });

        return run.Report("import layer links");
    }

    private static IEnumerable<ImportStepKind> KindsOf(ImportLayer layer)
        => Enum.GetValues<ImportStepKind>().Where(kind => ImportLayers.Of(kind) == layer);

    private static string LayerOrNone(ImportStepKind kind)
        => ImportLayers.Of(kind) is { } layer ? $"layer {layer}" : "no layer";
}
