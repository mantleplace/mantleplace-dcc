namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The ODbL decisions: which role a file has (and that a missing or unknown one reads as
/// <c>database</c>), which steps' output is filed in the ODbL container, how a container is named,
/// and what Prepare for handover does with each piece of content.
/// </summary>
/// <remarks>
/// The planner's choices are all here, headless. Where the importer files an element and how the
/// handover command removes one needs a Revit document and is checked by a real import.
/// </remarks>
internal static class OdblContentTests
{
    private const string Layout = """
        "layout": {
          "points_csv": "Surface/SurfacePoints.csv",
          "surface_dxf": "Surface/Surface.dxf",
          "buildings_ifc": "Site/Site.ifc",
          "landxml": "Surface/Surface.landxml",
          "contours": "Surface/Contours.dxf"
        }
        """;

    private static readonly string[] Bundle =
    [
        "README.md",
        "Metadata/manifest.json",
        "Surface/SurfacePoints.csv",
        "Surface/Surface.dxf",
        "Surface/Surface.landxml",
        "Surface/Contours.dxf",
        "Site/Site.ifc",
    ];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the reader keeps each file's stated role, licence and sources", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse("""
                {
                  "version": "1.14.0",
                  "file_entries": {
                    "Surface/SurfacePoints.csv": {
                      "odbl_role": "produced-work",
                      "license": "CC-BY-4.0",
                      "sources": [ { "name": "USGS 3DEP", "license": "CC0-1.0" } ]
                    },
                    "Site/Site.ifc": {
                      "odbl_role": "database",
                      "license": "ODbL-1.0",
                      "sources": [ { "name": "OpenStreetMap", "license": "ODbL-1.0" }, { "name": "", "license": "" } ]
                    },
                    "README.md": { "odbl_role": "none" }
                  }
                }
                """);

            run.Equal(manifest.FileEntries.Count, 3, "three entries");
            FileLicensing points = manifest.FileEntries["Surface/SurfacePoints.csv"];
            run.True(points.Role == OdblRole.ProducedWork, "produced-work");
            run.Equal(points.License, "CC-BY-4.0", "licence verbatim");
            run.Equal(points.Sources.Count, 1, "one source");
            run.Equal(points.Sources[0].Name, "USGS 3DEP", "source name");
            run.Equal(points.Sources[0].License, "CC0-1.0", "source licence");

            FileLicensing site = manifest.FileEntries["Site/Site.ifc"];
            run.True(site.Role == OdblRole.Database, "database");
            run.Equal(site.Sources.Count, 1, "an empty source is not kept");
            run.True(manifest.FileEntries["README.md"].Role == OdblRole.None, "none");
        });

        run.Case("a manifest without file_entries has none, and every file then reads as database", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse("""{"version": "1.13.0"}""");

            run.Equal(manifest.FileEntries.Count, 0, "no entries");
            run.True(OdblContent.RoleOf(manifest.FileEntries, "Site/Site.ifc") == OdblRole.Database, "missing entry is database");
        });

        run.Case("a missing role, an unknown role and an entry that is not an object all read as database", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse("""
                {
                  "version": "1.14.0",
                  "file_entries": {
                    "a": { "license": "CC0-1.0" },
                    "b": { "odbl_role": "derivative-work" },
                    "c": "database",
                    "d": { "odbl_role": null }
                  }
                }
                """);

            foreach (string path in new[] { "a", "b", "c", "d" })
            {
                run.True(OdblContent.RoleOf(manifest.FileEntries, path) == OdblRole.Database, $"{path} reads as database");
            }

            run.True(OdblContent.RoleOf(manifest.FileEntries, "not-listed") == OdblRole.Database, "an unlisted file reads as database");
        });

        run.Case("the role of several files is the strictest of them, and of no file is none", () =>
        {
            Dictionary<string, FileLicensing> entries = new()
            {
                ["pw"] = new FileLicensing { Role = OdblRole.ProducedWork },
                ["none"] = new FileLicensing { Role = OdblRole.None },
                ["db"] = new FileLicensing { Role = OdblRole.Database },
            };

            run.True(OdblContent.RoleOf(entries, ["none", "pw"]) == OdblRole.ProducedWork, "produced work beats none");
            run.True(OdblContent.RoleOf(entries, ["none", "pw", "db"]) == OdblRole.Database, "database beats all");
            run.True(OdblContent.RoleOf(entries, ["none"]) == OdblRole.None, "none alone");
            run.True(OdblContent.RoleOf(entries, ["none", "unlisted"]) == OdblRole.Database, "an unlisted file is a database");
            run.True(OdblContent.RoleOf(entries, Array.Empty<string>()) == OdblRole.None, "no file has no role to miss");
        });

        run.Case("the role words round-trip and only database is contained", () =>
        {
            foreach (OdblRole role in Enum.GetValues<OdblRole>())
            {
                run.True(OdblContent.ParseRole(OdblContent.RoleName(role)) == role, $"{role} round-trips");
            }

            run.True(OdblContent.IsContained(OdblRole.Database), "database is contained");
            run.False(OdblContent.IsContained(OdblRole.ProducedWork), "produced work is not");
            run.False(OdblContent.IsContained(OdblRole.None), "none is not");
        });

        run.Case("planned steps carry the role of the file they read", () =>
        {
            BundleImportPlan plan = Plan("""
                "file_entries": {
                  "Surface/SurfacePoints.csv": { "odbl_role": "produced-work" },
                  "Site/Site.ifc": { "odbl_role": "database" }
                }
                """);

            run.True(Step(plan, ImportStepKind.ToposurfaceFromPointsFile).Odbl == OdblRole.ProducedWork, "terrain is a produced work");
            run.True(Step(plan, ImportStepKind.ContextBuildings).Odbl == OdblRole.Database, "buildings are a database");
            run.True(Step(plan, ImportStepKind.LinkSiteIfc).Odbl == OdblRole.Database, "the IFC link is a database");
        });

        run.Case("a file with no entry makes database content, so a bundle from before 1.14.0 is all contained", () =>
        {
            BundleImportPlan plan = Plan(string.Empty);

            run.True(Step(plan, ImportStepKind.ToposurfaceFromPointsFile).Odbl == OdblRole.Database, "terrain");
            run.True(Step(plan, ImportStepKind.ContextBuildings).Odbl == OdblRole.Database, "buildings");
        });

        run.Case("a step that makes no content is never contained, whatever the manifest says", () =>
        {
            BundleImportPlan plan = Plan(string.Empty);

            foreach (ImportStep step in plan.Steps)
            {
                if (!ImportStepKinds.ImportsContent(step.Kind))
                {
                    run.True(step.Odbl == OdblRole.None, $"{step.Kind} is none");
                }
            }

            run.True(plan.Steps.Any(step => step.Kind == ImportStepKind.AttributionAndProvenance), "the plan has the attribution step");
        });

        run.Case("the terrain steps share one equivalence group and no other step has one", () =>
        {
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                bool terrain = kind is ImportStepKind.ToposurfaceFromPointsFile
                    or ImportStepKind.ToposurfaceFromSurfaceTin
                    or ImportStepKind.ToposurfaceFromSurfaceDxf;
                run.Equal(OdblContent.GroupOf(kind), terrain ? OdblContent.TerrainGroup : string.Empty, $"{kind}'s group");
            }
        });

        run.Case("a container's name round-trips to the order and group it was made for", () =>
        {
            string plain = OdblContent.ContainerName("f93bc782-ab12", string.Empty);
            string grouped = OdblContent.ContainerName("f93bc782-ab12", OdblContent.TerrainGroup);

            run.Equal(plain, "Mantle Place ODbL f93bc782-ab12", "plain name");
            run.Equal(grouped, "Mantle Place ODbL f93bc782-ab12 (terrain)", "grouped name");
            run.True(OdblContent.ParseContainerName(plain) == new ContainerKey("f93bc782-ab12", string.Empty), "plain parses back");
            run.True(OdblContent.ParseContainerName(grouped) == new ContainerKey("f93bc782-ab12", "terrain"), "grouped parses back");
            run.True(plain.StartsWith(SiteContext.StampPrefix, StringComparison.Ordinal), "named under the plugin's own prefix");
        });

        run.Case("a name that is not one of our containers parses to nothing", () =>
        {
            run.True(OdblContent.ParseContainerName(null) is null, "null");
            run.True(OdblContent.ParseContainerName("Mantle Place Site Context") is null, "the site context filter");
            run.True(OdblContent.ParseContainerName("Mantle Place ODbL") is null, "no identity");
            run.True(OdblContent.ParseContainerName("Mantle Place ODbL a b") is null, "two words are not an identity");
            run.True(OdblContent.ParseContainerName("Mantle Place ODbL a (terrain") is null, "an unclosed group");
            run.True(OdblContent.ParseContainerName("My ODbL set") is null, "someone else's set");
        });

        run.Case("handover removes ODbL content and leaves everything else exactly as it is", () =>
        {
            IReadOnlyList<HandoverDecision> decisions = OdblContent.PlanHandover(
            [
                new HandoverItem("a", IsOdbl: true, Group: string.Empty),
                new HandoverItem("a", IsOdbl: false, Group: string.Empty),
                new HandoverItem("b", IsOdbl: true, Group: string.Empty),
            ]);

            run.True(decisions[0] == HandoverDecision.Remove, "ODbL content with no equivalent is removed");
            run.True(decisions[1] == HandoverDecision.Keep, "other content is kept");
            run.True(decisions[2] == HandoverDecision.Remove, "another order's ODbL content is removed too");
        });

        run.Case("handover swaps ODbL terrain for a Produced Work terrain of the same order", () =>
        {
            IReadOnlyList<HandoverDecision> decisions = OdblContent.PlanHandover(
            [
                new HandoverItem("a", IsOdbl: true, Group: OdblContent.TerrainGroup),
                new HandoverItem("a", IsOdbl: false, Group: OdblContent.TerrainGroup),
            ]);

            run.True(decisions[0] == HandoverDecision.Swap, "the ODbL terrain is swapped");
            run.True(decisions[1] == HandoverDecision.Keep, "the Produced Work terrain stays");
        });

        run.Case("a Produced Work of another order, or of another group, is no equivalent", () =>
        {
            IReadOnlyList<HandoverDecision> decisions = OdblContent.PlanHandover(
            [
                new HandoverItem("a", IsOdbl: true, Group: OdblContent.TerrainGroup),
                new HandoverItem("b", IsOdbl: false, Group: OdblContent.TerrainGroup),
                new HandoverItem("c", IsOdbl: true, Group: OdblContent.TerrainGroup),
                new HandoverItem("c", IsOdbl: false, Group: string.Empty),
            ]);

            run.True(decisions[0] == HandoverDecision.Remove, "order b's terrain does not stand in for order a's");
            run.True(decisions[2] == HandoverDecision.Remove, "an ungrouped Produced Work does not stand in for terrain");
        });

        run.Case("an item with no group is never swapped, even beside an ungrouped Produced Work", () =>
        {
            IReadOnlyList<HandoverDecision> decisions = OdblContent.PlanHandover(
            [
                new HandoverItem("a", IsOdbl: true, Group: string.Empty),
                new HandoverItem("a", IsOdbl: false, Group: string.Empty),
            ]);

            run.True(decisions[0] == HandoverDecision.Remove, "removed");
        });

        run.Case("an empty project plans nothing", () =>
        {
            run.Equal(OdblContent.PlanHandover([]).Count, 0, "no decisions");
        });

        run.Case("a terrain stamp names the order it belongs to, and nothing else does", () =>
        {
            string stamp = TerrainIdentity.Stamp("f93bc782-ab12", "3f00000000000000", "EGM2008-orthometric");

            run.Equal(TerrainIdentity.StemOf(stamp), "f93bc782-ab12", "the stem");
            run.Equal(TerrainIdentity.StemOf(TerrainIdentity.Stamp("x", null)), "x", "a digest-less stamp");
            run.True(TerrainIdentity.StemOf("Mantle Place Road f93bc782-ab12/3") is null, "a road's stamp");
            run.True(TerrainIdentity.StemOf("a curator's note") is null, "free text");
            run.True(TerrainIdentity.StemOf(null) is null, "no comments");
            run.True(TerrainIdentity.StemOf("Mantle Place Terrain ") is null, "an empty stamp");
        });

        run.Case("the confirmation and the result say what happens, with the counts", () =>
        {
            HandoverCounts none = new(0, 0, 0);
            HandoverCounts some = new(2, 1, 340);

            run.Contains(HandoverSummary.Ask(none), "nothing will be removed", "nothing to remove");
            run.Contains(HandoverSummary.Ask(some), "340 elements", "elements");
            run.Contains(HandoverSummary.Ask(some), "2 containers", "containers");
            run.Contains(HandoverSummary.Ask(some), "1 container will be swapped", "the swap");
            run.Contains(HandoverSummary.Ask(some), "One undo step", "undo");
            run.False(HandoverSummary.Ask(new HandoverCounts(1, 0, 1)).Contains("swapped", StringComparison.Ordinal), "no swap, no mention");

            string done = HandoverSummary.Done(some, 345, "The credit is in the view.");
            run.Contains(done, "Removed 340 elements", "removed");
            run.Contains(done, "345 elements in all", "what Revit deleted along with them");
            run.Contains(done, "The credit is in the view.", "the credit line verbatim");
            run.Contains(done, "Nothing was saved", "saves nothing");
            run.Contains(HandoverSummary.Done(none, 0, "x"), "nothing was removed", "nothing removed");
            run.Contains(HandoverSummary.Done(new HandoverCounts(1, 0, 1), 1, "x"), "Removed 1 element of ODbL content from 1 container.", "singular");
        });

        return run.Report("OdblContent");
    }

    private static BundleImportPlan Plan(string fileEntries)
    {
        string entries = fileEntries.Length == 0 ? string.Empty : ", " + fileEntries;
        BundleManifest manifest = BundleManifestReader.Parse($$"""{"version": "1.14.0", {{Layout}}{{entries}}}""");
        return BundleImportPlanner.Plan(manifest, Bundle, _ => new ImageSize(2048, 2048));
    }

    private static ImportStep Step(BundleImportPlan plan, ImportStepKind kind)
        => plan.Steps.First(step => step.Kind == kind);
}
