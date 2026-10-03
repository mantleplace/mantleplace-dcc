namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// <c>HPS-52</c> for the three deliverables this host's block and the host-neutral pointers both
/// name: the terrain points, the surface DXF and the site IFC. The block's <c>path</c> is the file
/// placed; <c>layout</c>, then the detail block's own <c>path</c>, is the fallback for a block that
/// carries none.
/// </summary>
/// <remarks>
/// No bundle seen so far names two different files here, which is why every case is synthetic. The
/// hazard each one guards is a file in one frame placed with another file's statements, and nothing
/// on screen saying so.
/// </remarks>
internal static class HostBlockPrecedenceTests
{
    private const string BlockHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string DetailHash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    /// <summary>One deliverable: where each of its three pointers lives, and how the plan names it.</summary>
    private sealed record Deliverable(
        string Name,
        string LayoutKey,
        string DetailSection,
        string DetailKey,
        string BlockKey,
        Func<BundleManifest, BundleArtifact?> Read,
        ImportStepKind Kind);

    private static readonly Deliverable[] Deliverables =
    [
        new("terrain points", "points_csv", "elevation", "points_csv", "toposurface_points",
            m => m.ToposurfacePoints, ImportStepKind.ToposurfaceFromPointsFile),
        new("surface DXF", "surface_dxf", "elevation", "surface_dxf", "surface_dxf",
            m => m.SurfaceDxf, ImportStepKind.ToposurfaceFromSurfaceDxf),
        new("site IFC", "buildings_ifc", "buildings", "ifc", "ifc_site",
            m => m.SiteIfc, ImportStepKind.ContextBuildings),
    ];

    internal static int Run()
    {
        TestRun run = new();

        foreach (Deliverable d in Deliverables)
        {
            run.Case($"{d.Name}: the block's file is placed when it differs from layout's, with the block's statements", () =>
            {
                BundleManifest manifest = BundleManifestReader.Parse(
                    Manifest(d, layoutPath: "Shared/Layout.file", detailPath: "Shared/Layout.file", blockPath: "Revit/Own.file"));
                BundleArtifact? artifact = d.Read(manifest);

                run.True(manifest.IsValid, $"accepted ({manifest.Error})");
                run.Equal(artifact?.Path, "Revit/Own.file", "the block's pointer is the placement path");
                run.Equal(artifact?.Units, "m", "the block's units");
                run.Equal(artifact?.HorizontalFrame, "local_enu", "the block's frame");
                run.Equal(artifact?.Sha256, BlockHash, "the block's hash");
                run.True(artifact?.VerticalDatum is null, "the other file's datum is not transplanted");
                run.True(artifact?.NamedByOwnBlock ?? false, "the block named the file placed");
            });

            run.Case($"{d.Name}: a layout pointer with no detail path is not described onto the block's file", () =>
            {
                // The detail block with no `path` of its own describes layout's file, so its units
                // must not cross to the block's file either.
                BundleManifest manifest = BundleManifestReader.Parse(
                    Manifest(d, layoutPath: "Shared/Layout.file", detailPath: null, blockPath: "Revit/Own.file", blockUnits: null));
                BundleArtifact? artifact = d.Read(manifest);

                run.Equal(artifact?.Path, "Revit/Own.file", "the block's pointer wins");
                run.True(artifact?.Units is null, "layout's file's units are not carried over");
            });

            run.Case($"{d.Name}: a block with no path leaves layout's file and the detail block's statements, as before", () =>
            {
                BundleManifest manifest = BundleManifestReader.Parse(
                    Manifest(d, layoutPath: "Shared/Layout.file", detailPath: "Shared/Layout.file", blockPath: null, blockUnits: null, blockFrame: null));
                BundleArtifact? artifact = d.Read(manifest);

                run.True(manifest.IsValid, $"accepted ({manifest.Error})");
                run.Equal(artifact?.Path, "Shared/Layout.file", "layout's file is the fallback");
                run.Equal(artifact?.Units, "ftUS", "the detail block's units");
                run.Equal(artifact?.VerticalDatum, "NAVD88", "the detail block's datum");
                run.Equal(artifact?.Sha256, BlockHash, "the pathless block still describes the file it covers");
            });

            run.Case($"{d.Name}: a bundle with no block at all reads layout's file and the detail block, as before", () =>
            {
                BundleManifest manifest = BundleManifestReader.Parse(
                    Manifest(d, layoutPath: "Shared/Layout.file", detailPath: "Shared/Layout.file", blockPath: null, withBlock: false));
                BundleArtifact? artifact = d.Read(manifest);

                run.Equal(artifact?.Path, "Shared/Layout.file", "layout's file");
                run.Equal(artifact?.Units, "ftUS", "the detail block's units");
                run.Equal(artifact?.Sha256, DetailHash, "the detail block's hash");
                run.False(artifact?.NamedByOwnBlock ?? true, "no block named it");
            });

            run.Case($"{d.Name}: agreeing pointers read as before", () =>
            {
                BundleManifest manifest = BundleManifestReader.Parse(
                    Manifest(d, layoutPath: "Shared/Same.file", detailPath: "Shared/Same.file", blockPath: "Shared/Same.file"));
                BundleArtifact? artifact = d.Read(manifest);

                run.Equal(artifact?.Path, "Shared/Same.file", "the one file");
                run.Equal(artifact?.Units, "m", "the block's units, read first");
                run.Equal(artifact?.HorizontalFrame, "local_enu", "the block's frame");
                run.Equal(artifact?.VerticalDatum, "NAVD88", "the detail block describes the same file, so its datum stays");
                run.Equal(artifact?.Sha256, BlockHash, "the block's hash");
                run.True(artifact?.NamedByOwnBlock ?? false, "named by the block");
            });

            run.Case($"{d.Name}: the planner places the block's entry, not layout's, when the bundle holds both", () =>
            {
                BundleManifest manifest = BundleManifestReader.Parse(
                    Manifest(d, layoutPath: "Shared/Layout.file", detailPath: "Shared/Layout.file", blockPath: "Revit/Own.file"));
                BundleImportPlan plan = BundleImportPlanner.Plan(manifest, ["Shared/Layout.file", "Revit/Own.file"], _ => null);

                ImportStep? step = plan.Steps.FirstOrDefault(s => s.Kind == d.Kind);
                run.Equal(step?.EntryName, "Revit/Own.file", "the block's entry is the one placed");
                run.Equal(step?.ExpectedSha256, BlockHash, "and verified against the block's hash");
                run.False(plan.Steps.Any(s => s.EntryName == "Shared/Layout.file"), "layout's file is not placed");
            });

            run.Case($"{d.Name}: a block path to an entry the bundle lacks is a missing pointer, never layout's file (HPS-32)", () =>
            {
                BundleManifest manifest = BundleManifestReader.Parse(
                    Manifest(d, layoutPath: "Shared/Layout.file", detailPath: "Shared/Layout.file", blockPath: "Revit/Own.file"));
                BundleImportPlan plan = BundleImportPlanner.Plan(manifest, ["Shared/Layout.file"], _ => null);

                run.False(plan.Steps.Any(s => s.EntryName == "Shared/Layout.file"), "layout's file is not placed in its stead");
                SkippedImport? skip = plan.Skipped.FirstOrDefault(s => s.Kind == d.Kind);
                run.True(
                    skip?.ReasonCode == SkipReasonCode.EntryNotInArchive,
                    $"skipped as a pointer to a missing entry, got {skip?.ReasonCode}");
                run.Contains(skip?.Reason, "Revit/Own.file", "naming the block's path");
            });
        }

        return run.Report("host block precedence");
    }

    private static string Manifest(
        Deliverable d,
        string layoutPath,
        string? detailPath,
        string? blockPath,
        string? blockUnits = "m",
        string? blockFrame = "local_enu",
        bool withBlock = true)
    {
        static string Field(string name, string? value) => value is null ? string.Empty : $"\"{name}\": \"{value}\", ";

        string detail = "{ " + Field("path", detailPath) + Field("units", "ftUS") + Field("vertical_datum", "NAVD88")
            + $"\"sha256\": \"{DetailHash}\" }}";
        string block = "{ " + Field("path", blockPath) + Field("units", blockUnits) + Field("horizontal_frame", blockFrame)
            + $"\"sha256\": \"{BlockHash}\" }}";
        string hosts = withBlock
            ? $", \"hosts\": {{ \"revit\": {{ \"{d.BlockKey}\": {block} }} }}"
            : ", \"hosts\": { \"unreal\": {} }";

        return "{ \"version\": \"1.0.0\", "
            + $"\"layout\": {{ \"{d.LayoutKey}\": \"{layoutPath}\" }}, "
            + $"\"{d.DetailSection}\": {{ \"{d.DetailKey}\": {detail} }}"
            + hosts
            + " }";
    }
}
