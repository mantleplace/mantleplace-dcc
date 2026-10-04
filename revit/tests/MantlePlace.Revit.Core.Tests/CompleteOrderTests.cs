using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// A complete order imports from the vault without a Prepare: the route one materialize start
/// decides, the download view's request and response, and the view held in the cache and opened.
/// </summary>
/// <remarks>
/// The wire shapes below are the platform's own: the no-op and the <c>503 delivery_state_unknown</c>
/// of the materialize start, and the host-view response of <c>POST …/download</c>, whose
/// <c>view</c> block, <c>sizeBytes</c> and <c>manifestSha256</c> are what a complete bundle answers
/// and a bundle stored as one zip does not.
/// </remarks>
internal static class CompleteOrderTests
{
    private const string OrderId = "3f285101-0310-425b-b06b-bdb73b025b6a";

    private const string PointsCsv = "1,2,3\n4,5,6\n7,8,9\n";

    internal static int Run()
    {
        TestRun run = new();

        RouteCases(run);
        RequestCases(run);
        ResponseCases(run);
        DecisionCases(run);

        string sandbox = Path.Combine(Path.GetTempPath(), "mp-complete-order-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        try
        {
            CacheCases(run, sandbox);
            ViewArchiveCases(run, sandbox);
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return run.Report("complete orders");
    }

    // ------------------------------------------------------------------ the start decides the route

    private static void RouteCases(TestRun run)
    {
        run.Case("noop: a complete order downloads its view, carrying what it delivered", () =>
        {
            ImportRouteDecision decision = CompleteOrders.FromStart(
                200, """{"noop":true,"delivered":["elevation.points_csv","landcover.tree_points_csv"]}""");

            run.True(decision.Route == ImportRoute.DownloadView, "downloads the view");
            run.Equal(decision.Delivered?.Count ?? -1, 2, "the delivered set rides along");
        });

        run.Case("503 delivery_state_unknown: try the download, delivered set unknown", () =>
        {
            ImportRouteDecision decision = CompleteOrders.FromStart(
                503,
                """{"error":"Couldn't confirm which formats this bundle already has. Try again in a moment.","code":"delivery_state_unknown"}""");

            run.True(decision.Route == ImportRoute.DownloadView, "tries the download, not a refusal");
            run.True(decision.Delivered is null, "nothing is known about what was delivered");
        });

        run.Case("a 503 with any other code is a refusal, in the platform's words", () =>
        {
            ImportRouteDecision decision = CompleteOrders.FromStart(503, """{"error":"Down for maintenance","code":"maintenance"}""");

            run.True(decision.Route == ImportRoute.Refused, "refused");
            run.Contains(decision.Message, "Down for maintenance", "the platform's message");
        });

        // Every on-demand outcome: a job started, joined, coalesced, or running unnamed.
        (string Name, int Status, string Body)[] onDemand =
        [
            ("201 started", 201, """{"jobId":"job-1","tokens":["buildings.ifc"]}"""),
            ("409 active_job", 409, """{"error":"A job is already running","code":"active_job","activeJobId":"job-2"}"""),
            ("200 coalesced", 200, """{"coalesced":true,"jobId":"job-3","tokens":["buildings.ifc"]}"""),
            ("409 running, unnamed", 409, """{"error":"A job is already running","code":"active_job","activeJobId":null}"""),
        ];

        foreach ((string name, int status, string body) in onDemand)
        {
            run.Case($"{name}: an on-demand order runs the existing Prepare", () =>
            {
                ImportRouteDecision decision = CompleteOrders.FromStart(status, body);
                run.True(decision.Route == ImportRoute.Prepare, "Prepare follows it");
            });
        }

        run.Case("400 not_ready: an order still building is refused with the platform's reason", () =>
        {
            ImportRouteDecision decision = CompleteOrders.FromStart(
                400, """{"error":"This order is still being built","code":"not_ready"}""");

            run.True(decision.Route == ImportRoute.Refused, "refused");
            run.Contains(decision.Message, "still being built", "the platform's message");
        });

        run.Case("the retired 202 queued body is no longer read as a success", () =>
        {
            ImportRouteDecision decision = CompleteOrders.FromStart(
                202, """{"queued":true,"pendingTokens":["buildings.ifc"]}""");

            run.True(decision.Route == ImportRoute.Refused, "refused, not parked");
            run.Contains(decision.Message, "named no job to poll", "the unrecognised-shape refusal");
        });

        run.Case("a non-2xx that explains nothing is still a refusal", () =>
        {
            ImportRouteDecision decision = CompleteOrders.FromStart(502, "<html>Bad gateway</html>");

            run.True(decision.Route == ImportRoute.Refused, "refused");
            run.Contains(decision.Message, "HTTP 502", "names the status");
        });
    }

    // ------------------------------------------------------------------ the view request

    private static void RequestCases(TestRun run)
    {
        run.Case("the download request asks for host revit, MAX at every entry, as the whole-bundle format", () =>
        {
            using JsonDocument body = JsonDocument.Parse(HostViews.BuildRequestBody());
            JsonElement root = body.RootElement;

            run.Equal(root.GetProperty("format").GetString(), "bundle", "a view is cut from the bundle");
            JsonElement view = root.GetProperty("view");
            run.Equal(view.GetProperty("host").GetString(), "revit", "this host's block");

            JsonElement levels = view.GetProperty("levels");
            run.Equal(levels.EnumerateObject().Count(), 1, "one rule");
            run.Equal(levels.GetProperty("*").GetString(), "MAX", "every entry at MAX, which holds every cut level");
        });
    }

    // ------------------------------------------------------------------ the view response

    private static void ResponseCases(TestRun run)
    {
        run.Case("a host-view response reads its view block, size and manifest digest", () =>
        {
            string? error = HostViews.TryParse(ViewResponse(partial: false, "elevation.points_csv@MAX", "landcover.tree_points_csv@MAX"), out HostViewLink link);

            run.Equal(error, null, "parsed");
            run.Equal(link.Url, "https://mantle.place/v/abc", "the link");
            run.True(link.IsView, "a view was cut");
            run.False(link.Partial, "sealed");
            run.Equal(link.Parts.Count, 2, "its parts");
            run.True(link.SizeBytes == 86_998_475L, "its size");
            run.Equal(link.ManifestSha256, new string('a', 64), "its manifest digest");
        });

        run.Case("a bundle stored as one zip answers with no view block: the whole archive", () =>
        {
            string? error = HostViews.TryParse(
                """{"url":"https://r2.example/z.zip","expiresAt":"2099-01-01T00:00:00Z","view":null,"sizeBytes":null,"manifestSha256":null}""",
                out HostViewLink link);

            run.Equal(error, null, "parsed");
            run.False(link.IsView, "no view");
            run.True(link.SizeBytes is null, "no view size");
            run.True(link.ManifestSha256 is null, "no manifest digest");
        });

        run.Case("a response with no url is a refusal, in the platform's words", () =>
        {
            string? error = HostViews.TryParse("""{"error":"Bundle not found","code":"not_ready"}""", out _);
            run.Equal(error, "Bundle not found", "the platform's message");
        });
    }

    // ------------------------------------------------------------------ what to fetch

    private static void DecisionCases(TestRun run)
    {
        string[] withTrees = ["landcover.tree_points_csv", "elevation.points_csv"];

        run.Case("a sealed view holding the tree points is enough", () =>
        {
            HostViewLink link = Parse(ViewResponse(false, "elevation.points_csv@MAX", "landcover.tree_points_csv@MAX"));
            run.True(HostViews.Decide(link, withTrees) == ViewFetch.View, "the view");
        });

        run.Case("a view without the tree points the order delivered fetches the whole bundle instead", () =>
        {
            // Today's platform: the host view holds hosts.revit's files only, and the tree points
            // live outside that block.
            HostViewLink link = Parse(ViewResponse(false, "elevation.points_csv@MAX", "revit_vectors"));
            run.True(HostViews.Decide(link, withTrees) == ViewFetch.WholeBundleInstead, "the whole bundle");
            run.Equal(HostViews.MissingFrom(link.Parts, withTrees).Count, 1, "one file would go missing");
        });

        run.Case("an order that delivered no tree points needs none from the view", () =>
        {
            HostViewLink link = Parse(ViewResponse(false, "elevation.points_csv@MAX"));
            run.True(HostViews.Decide(link, ["elevation.points_csv"]) == ViewFetch.View, "the view");
        });

        run.Case("with the delivered set unknown, the view must hold the tree points itself", () =>
        {
            HostViewLink link = Parse(ViewResponse(false, "elevation.points_csv@MAX"));
            run.True(HostViews.Decide(link, null) == ViewFetch.WholeBundleInstead, "the whole bundle");
        });

        run.Case("a partial view means the order is still building: the Prepare follows it", () =>
        {
            HostViewLink link = Parse(ViewResponse(true, "elevation.points_csv@MAX", "landcover.tree_points_csv@MAX"));
            run.True(HostViews.Decide(link, withTrees) == ViewFetch.Prepare, "Prepare");
        });

        run.Case("no view block after a no-op: the whole archive the platform already linked", () =>
        {
            HostViewLink link = Parse("""{"url":"https://r2.example/z.zip","expiresAt":"","view":null}""");
            run.True(HostViews.Decide(link, withTrees) == ViewFetch.WholeArchive, "the whole archive");
        });

        run.Case("no view block after a 503 delivery_state_unknown: unproven, so the Prepare follows it", () =>
        {
            // Only a view block proves a complete order when the start proved nothing. An on-demand
            // order's single zip is not imported on the strength of an unreadable delivery state.
            HostViewLink link = Parse("""{"url":"https://r2.example/z.zip","expiresAt":"","view":null}""");
            run.True(HostViews.Decide(link, null) == ViewFetch.Prepare, "Prepare");
        });

        run.Case("a part's token is everything before the level", () =>
        {
            run.Equal(HostViews.TokenOfPart("landcover.tree_points_csv@MED"), "landcover.tree_points_csv", "token@level");
            run.Equal(HostViews.TokenOfPart("revit_vectors"), "revit_vectors", "a tokenless part is its own id");
        });
    }

    // ------------------------------------------------------------------ the view in the cache

    private static void CacheCases(TestRun run, string sandbox)
    {
        run.Case("a cached view is checked against its own record, never the listing's whole-archive facts", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "cache-a"));
            string zip = WriteViewZip(Path.Combine(sandbox, "a.zip"));
            byte[] bytes = File.ReadAllBytes(zip);

            string? error = PromoteView(cache, bytes, LocalBundleArchive.ManifestSha256(zip)).GetAwaiter().GetResult();
            run.Equal(error, null, "promoted");

            // The listing describes the 441 MB archive, not this view.
            CacheEntry entry = cache.Inspect(OrderId, 441_349_052L, new string('b', 64), ManifestVersions.MinSupportedManifestVersion);
            run.True(entry.State == CacheState.CachedValid, "still importable, not 'the wrong size'");
            run.True(entry.Sidecar?.HoldsView == true, "the sidecar says it is a view");
            run.False(entry.Verdict.IntegrityChecked, "no zip digest was compared — the files are, at import");
        });

        run.Case("a view whose manifest does not match the stated digest is discarded before the rename", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "cache-b"));
            byte[] bytes = File.ReadAllBytes(WriteViewZip(Path.Combine(sandbox, "b.zip")));

            string? error = PromoteView(cache, bytes, new string('0', 64)).GetAwaiter().GetResult();
            run.Contains(error, "manifest did not match", "refused");
            run.False(File.Exists(cache.LayoutFor(OrderId).BundleZipPath), "nothing promoted");
        });

        run.Case("the sidecar records a view and reads it back; a whole archive's reads as it always has", () =>
        {
            CacheSidecar view = new() { OrderId = OrderId, SizeBytes = 10, HoldsView = true };
            run.True(CacheSidecars.TryParse(CacheSidecars.Serialize(view))?.HoldsView == true, "round trip");

            string whole = CacheSidecars.Serialize(new CacheSidecar { OrderId = OrderId, SizeBytes = 10 });
            run.False(whole.Contains("\"view\"", StringComparison.Ordinal), "no view key on a whole archive");
            run.False(CacheSidecars.TryParse(whole)?.HoldsView ?? true, "reads as not a view");
        });
    }

    // ------------------------------------------------------------------ the view opened

    private static void ViewArchiveCases(TestRun run, string sandbox)
    {
        run.Case("a view whose manifest names parts the zip does not hold imports with no integrity refusal", () =>
        {
            string zip = WriteViewZip(Path.Combine(sandbox, "c.zip"));
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, Path.Combine(sandbox, "cache-c"));

            run.True(archive.Manifest is not null, "the manifest parsed");
            BundleImportPlan plan = BundleImportPlanner.Plan(archive.Manifest!, archive.EntryNames, archive.ProbeImageSize);

            run.True(plan.CanImport, "importable");
            run.True(
                plan.Steps.Any(step => step.Kind == ImportStepKind.ToposurfaceFromPointsFile),
                "the terrain the view carries is planned");
            run.Equal(archive.VerifyPlan(plan), null, "no integrity refusal");
            run.False(
                plan.Skipped.Any(skip => skip.Reason.Contains("Mesh/", StringComparison.Ordinal)
                    || skip.Reason.Contains("Landcover/LandCover.tif", StringComparison.Ordinal)),
                "no file the view left out is named as missing");
        });
    }

    // ------------------------------------------------------------------ fixtures

    private static HostViewLink Parse(string body)
    {
        HostViews.TryParse(body, out HostViewLink link);
        return link;
    }

    private static string ViewResponse(bool partial, params string[] parts)
        => "{\"url\":\"https://mantle.place/v/abc\",\"expiresAt\":\"2099-01-01T00:00:00Z\","
            + "\"view\":{\"kind\":\"host\",\"partial\":" + (partial ? "true" : "false") + ",\"snapshot\":2,\"parts\":["
            + string.Join(",", parts.Select(part => "\"" + part + "\""))
            + "],\"missing\":[],\"file_count\":15},"
            + "\"sizeBytes\":86998475,\"manifestSha256\":\"" + new string('a', 64) + "\"}";

    private static Task<string?> PromoteView(BundleCache cache, byte[] bytes, string manifestSha256)
        => cache.PromoteAsync(
            OrderId,
            async (destination, token) => await destination.WriteAsync(bytes, token).ConfigureAwait(false),
            bytes.LongLength,
            expectedSha256: null,
            ManifestVersions.MinSupportedManifestVersion,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None,
            holdsView: true,
            expectedManifestSha256: manifestSha256);

    /// <summary>
    /// A view the way the platform cuts one: Revit's terrain points and the whole-bundle manifest,
    /// whose <c>files</c> block still names the mesh and land-cover parts the view left out.
    /// </summary>
    private static string WriteViewZip(string zipPath)
    {
        string manifest = $$"""
            {
              "version": "1.0.0",
              "order_id": "{{OrderId}}",
              "layout": { "manifest": "Metadata/manifest.json", "points_csv": "Surface/SurfacePoints.csv" },
              "hosts": {
                "revit": {
                  "georeference": {
                    "crs_projected": "EPSG:32610",
                    "origin": {
                      "lon": -122.47853042317121,
                      "lat": 37.83126164839943,
                      "projected": { "epsg": 32610, "easting": 545888.5, "northing": 4187221.5, "linear_unit": "m" }
                    }
                  },
                  "toposurface_points": {
                    "path": "Surface/SurfacePoints.csv",
                    "horizontal_frame": "local_enu",
                    "units": "m",
                    "sha256": "{{Sha256Digest.OfUtf8(PointsCsv)}}"
                  }
                }
              },
              "files": {
                "Surface/SurfacePoints.csv": { "part": "elevation.points_csv@MAX", "size_bytes": 18, "sha256": "{{Sha256Digest.OfUtf8(PointsCsv)}}" },
                "Mesh/Terrain.glb": { "part": "mesh.glb@MAX", "size_bytes": 1000, "sha256": "{{new string('1', 64)}}" },
                "Mesh/Terrain.MED.glb": { "part": "mesh.glb@MED", "size_bytes": 500, "sha256": "{{new string('2', 64)}}" },
                "Landcover/LandCover.tif": { "part": "landcover.worldcover_tif@MAX", "size_bytes": 800, "sha256": "{{new string('3', 64)}}" }
              },
              "view": {
                "kind": "host",
                "partial": false,
                "snapshot": 2,
                "parts": ["elevation.points_csv@MAX"],
                "missing": [],
                "file_count": 2
              }
            }
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (FileStream stream = File.Create(zipPath))
        using (ZipArchive builder = new(stream, ZipArchiveMode.Create))
        {
            WriteEntry(builder, "Metadata/manifest.json", manifest);
            WriteEntry(builder, "Surface/SurfacePoints.csv", PointsCsv);
        }

        return zipPath;
    }

    private static void WriteEntry(ZipArchive builder, string name, string content)
    {
        using Stream entry = builder.CreateEntry(name).Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        entry.Write(bytes, 0, bytes.Length);
    }
}
