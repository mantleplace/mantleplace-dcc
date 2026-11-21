using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using MantlePlace.Client;

namespace MantlePlace.Core.Tests;

/// <summary>
/// A 3.0 order's level, chosen before the download (<see cref="DownloadLevels"/>, <see cref="LevelControl"/>):
/// the offer a listing row carries, the control the shared vault window shows in every host, the
/// request that names the host's files and one level, the cache slot it lands in, and a 2.x order
/// left as it was. The listing and the request are held to the vendored Platform API description,
/// whose 3.0 examples are the test double of the platform.
/// </summary>
internal static class DownloadLevelTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("a 3.0 listing row carries its levels, sizes, counts, Extras and the recommendation", () =>
        {
            VaultBundle large = ListedExample("mpb3LargeSite");
            run.True(large.Levels is not null, "the offer is read");
            LevelOffer offer = large.Levels!;
            run.Equal(offer.Recommended, FidelityLevel.Min, "Min is recommended for the large site, to Revit");
            run.Equal(offer.Levels.Count, 3, "three levels");
            run.True(offer.Levels.Select(level => level.Level).SequenceEqual(DownloadLevels.Offered), "Max, Med, Min in order");
            run.True(offer.SizeOf(FidelityLevel.Max).SizeBytes == 1811415040L, "Max's size");
            run.True(offer.SizeOf(FidelityLevel.Min).SizeBytes == 1227463066L, "Min's size");
            run.True(offer.SizeOf(FidelityLevel.Med).Triangles == 48000000L, "Med's counts");
            run.Equal(offer.Extras.Count, 3, "three Extras");
            run.Equal(offer.Extras[0].Name, "Site Brief", "named as a curator sees them");

            run.Equal(ListedExample("mpb3SmallSite").Levels!.Recommended, FidelityLevel.Max, "Max for the small site");
        });

        run.Case("a free site's plan sheets and terrain tiles are locked rows with no download; a large site's are not", () =>
        {
            LevelControl small = LevelControl.For(ListedExample("mpb3SmallSite"))!;
            run.True(small.Offer.Extras.Select(extra => extra.Locked).SequenceEqual([false, true, true]), "the Site Brief is never locked");
            run.Equal(WindowLabels.LockedExtrasWords, "More Extras come with paid sites", "the founder's words");
            run.True(
                small.LockedRows.SequenceEqual(
                [
                    "Plan sheets — More Extras come with paid sites",
                    "Terrain tiles — More Extras come with paid sites",
                ]),
                "one locked row for each, in the platform's order");

            LevelControl large = LevelControl.For(ListedExample("mpb3LargeSite"))!;
            run.True(large.Offer.Extras.All(extra => !extra.Locked), "a kept or paid bundle lists them unlocked");
            run.Equal(large.LockedRows.Count, 0, "so no locked row");

            run.True(
                VaultListingReader.TryParse("""{ "bundles": [ { "id": "c", "mpb3": { "version": "3.0", "recommendedLevel": "Max", "levels": { "Max": { "sizeBytes": 1 }, "Med": { "sizeBytes": 1 }, "Min": { "sizeBytes": 1 } }, "extras": [ { "id": "PlanSheets", "name": "Plan sheets", "sizeBytes": 5 } ] } } ] }""", out VaultListing older) is null,
                "parsed");
            run.False(older.Bundles[0].Levels!.Extras[0].Locked, "a platform that does not say is a platform that does not lock");
        });

        run.Case("a 2.x row has no levels, and a 3.0 offer that cannot be read is said, not guessed", () =>
        {
            run.True(VaultListingReader.TryParse("""{ "bundles": [ { "id": "a", "manifestVersion": "2.4.0" } ] }""", out VaultListing old) is null, "parsed");
            run.True(old.Bundles[0].Levels is null, "no offer on a 2.x row");
            run.Equal(old.Warnings.Count, 0, "and nothing to warn about");

            run.True(VaultListingReader.TryParse("""{ "bundles": [ { "id": "b", "mpb3": { "version": "3.0", "recommendedLevel": "Huge", "levels": {} } } ] }""", out VaultListing odd) is null, "parsed");
            run.True(odd.Bundles[0].Levels is null, "no offer");
            run.Equal(odd.Warnings.Count, 1, "one warning");
        });

        run.Case("the level control: Max, Med and Min with sizes, on the recommendation, the curator free to choose", () =>
        {
            VaultBundle large = ListedExample("mpb3LargeSite");
            LevelControl control = LevelControl.For(large)!;

            run.Equal(LevelControl.Heading, "Fidelity Level", "the glossary's term beside it");
            run.Equal(control.Options.Count, 3, "three choices");
            run.Equal(control.Options[0].Label, "Max — 1.8 GB", "Max with its size");
            run.Equal(control.Options[1].Label, "Med — 1.4 GB", "Med with its size");
            run.Equal(control.Options[2].Label, "Min — 1.2 GB (recommended)", "Min with its size, marked as the recommendation");
            run.True(control.Options[2].IsRecommended && !control.Options[0].IsRecommended && !control.Options[1].IsRecommended, "one recommendation");
            run.Equal(control.Options[1].ToolTip, "Med: 700,000 rows, 212,000 features, 48,000,000 triangles, 16,777,216 pixels", "the counts behind the size");
            run.Equal(control.Selected, FidelityLevel.Min, "preselected on the recommendation");
            run.Equal(control.SelectedIndex, 2, "the control shows it");

            run.True(control.Choose(FidelityLevel.Max), "the curator chooses Max");
            run.Equal(control.Selected, FidelityLevel.Max, "and Max is what downloads");
            run.Equal(control.SlotLevel, "MAX", "into the Max slot");
            run.False(control.Choose(FidelityLevel.Raw), "Raw is not a 3.0 level");
            run.Equal(control.Selected, FidelityLevel.Max, "and the choice stands");

            run.True(LevelControl.For(Two()) is null, "a 2.x order shows no control");
            run.True(LevelControl.For(null) is null, "nor does no order");
        });

        run.Case("an export from the web vault names no level: the control opens, and the fetch goes, on the recommendation", () =>
        {
            VaultBundle large = ListedExample("mpb3LargeSite");
            LevelControl first = LevelControl.For(large)!;
            first.Choose(FidelityLevel.Med);

            run.Equal(LevelControl.For(large)!.Selected, FidelityLevel.Min, "built again for the export, it is on the recommendation");
            run.Equal(DownloadLevels.SlotLevel(large), "MIN", "the export's cache slot is the recommendation's");
            run.Equal(DownloadLevels.SlotLevel(Two()), HostViews.EveryEntryLevel, "a 2.x order's slot is the one it always was");
        });

        run.Case("a received send's window opens on the recommendation; another level downloads into the slot before Import", () =>
        {
            VaultBundle large = ListedExample("mpb3LargeSite");
            LevelSwitch sent = new(LevelControl.For(large)!);

            run.Equal(sent.Control.Selected, FidelityLevel.Min, "the window a send opens is on the recommendation");
            run.Equal(sent.Held, FidelityLevel.Min, "which is the level the send was fetched at");
            run.True(sent.CanImport, "so Import may run at once");
            run.True(sent.Choose(FidelityLevel.Min) is null, "choosing the level the slot holds fetches nothing");

            run.True(sent.Choose(FidelityLevel.Med) == FidelityLevel.Med, "another level is downloaded");
            run.False(sent.CanImport, "and Import waits for it");
            run.True(sent.Choose(FidelityLevel.Max) is null, "one download at a time");
            sent.Fetched(ready: true);
            run.Equal(sent.Held, FidelityLevel.Med, "the slot now holds Med");
            run.True(sent.CanImport, "and Import reads it");

            run.True(sent.Choose(FidelityLevel.Max) == FidelityLevel.Max, "another choice");
            sent.Fetched(ready: false);
            run.Equal(sent.Control.Selected, FidelityLevel.Med, "a failed download puts the control back on the level the slot holds");
            run.True(sent.CanImport, "and Import reads that");

            run.Equal(LevelControl.At(large, FidelityLevel.Med)!.Selected, FidelityLevel.Med, "a window reopened on a fresh download is on its level");
            run.Equal(LevelControl.At(large, FidelityLevel.Raw)!.Selected, FidelityLevel.Min, "a level not offered leaves it on the recommendation");
            run.True(LevelControl.At(Two(), FidelityLevel.Min) is null, "a 2.x order shows no control");
        });

        run.Case("the 3.0 request names this host's files and one level; the 2.x request is unchanged", () =>
        {
            JsonNode three = JsonNode.Parse(DownloadLevels.BuildRequestBody(FidelityLevel.Min, ["Elevation/Contours.dxf", "Site/Site.ifc"]))!;
            run.Equal((string?)three["format"], "bundle", "the whole-bundle format");
            run.Equal((string?)three["mpb3"]!["version"], "3.0", "the 3.0 form");
            run.Equal((string?)three["mpb3"]!["level"], "Min", "one level");
            run.Equal(three["mpb3"]!["files"]!.AsArray().Count, 2, "the files named");
            run.True(three["view"] is null, "never with a view");

            run.Equal(
                HostViews.BuildRequestBody(),
                "{\"format\":\"bundle\",\"view\":{\"host\":\"" + HostIdentity.Id + "\",\"levels\":{\"*\":\"MAX\"}}}",
                "the 2.x view request is today's");
        });

        run.Case("a chosen level stands when the window re-seats the row; the recommendation is only the start", () =>
        {
            VaultBundle large = ListedExample("mpb3LargeSite");
            LevelChoices choices = new();

            LevelControl shown = choices.ControlFor(large)!;
            run.Equal(shown.Selected, FidelityLevel.Min, "before any choice, the control is on the recommendation");
            run.True(choices.Choose(shown, FidelityLevel.Med), "the curator chooses Med");

            // A row refresh clears the list's selection and seats the row again: the window asks for
            // the control twice, once for no row and once for the same order, now a fresh listing row.
            run.True(choices.ControlFor(null) is null, "no row selected shows no control");
            LevelControl reseated = choices.ControlFor(ListedExample("mpb3LargeSite"))!;
            run.Equal(reseated.Selected, FidelityLevel.Med, "the control still shows the choice");
            run.Equal(reseated.SlotLevel, "MED", "and Download requests that level");

            run.False(choices.Choose(reseated, FidelityLevel.Raw), "a level not offered is no choice");
            run.Equal(choices.ControlFor(large)!.Selected, FidelityLevel.Med, "and the choice stands");

            run.True(choices.Choose(reseated, FidelityLevel.Max), "the curator chooses again");
            run.Equal(choices.ControlFor(large)!.Selected, FidelityLevel.Max, "the latest choice stands");

            run.True(choices.ControlFor(Two()) is null, "a 2.x order has no control, as before");
            run.True(new LevelChoices().ControlFor(large)!.Selected == FidelityLevel.Min, "a window with no choice is on the recommendation");
        });

        run.Case("Revit's 3.0 files are files the published description promises", () =>
            AssertPromised(run, BundleImportPlanner.ThreeFiles, "Revit"));

        run.Case("the vault client asks for a 3.0 order at the chosen level, or the recommendation; a 2.x order as before", () =>
        {
            string[] files = ["Elevation/Contours.dxf"];
            VaultBundle large = ListedExample("mpb3LargeSite");

            run.True(SameJson(SentBody(large, FidelityLevel.Med, files), DownloadLevels.BuildRequestBody(FidelityLevel.Med, files)), "the chosen level and the host's files");
            run.True(SameJson(SentBody(large, null, files), DownloadLevels.BuildRequestBody(FidelityLevel.Min, files)), "no level is the recommendation");
            run.True(SameJson(SentBody(Two(), FidelityLevel.Min, files), HostViews.BuildRequestBody()), "a 2.x order sends its view request");
        });

        run.Case("the slot records the level: the same host and level hit it, another level misses and replaces it", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "mantleplace-levels-" + Guid.NewGuid().ToString("N"));
            try
            {
                VaultBundle large = ListedExample("mpb3LargeSite");
                BundleCache cache = new(root, HostIdentity.Id);
                Promote(cache, large, FidelityLevel.Med, "med");

                run.Equal(Inspect(cache, large, FidelityLevel.Med).State, CacheState.CachedValid, "the same level hits");
                CacheEntry other = Inspect(cache, large, FidelityLevel.Max);
                run.True(other.State != CacheState.CachedValid, "another level misses");
                run.Equal(other.Verdict.Reason, CacheInvalidReason.NotThisSlot, "as not this slot");
                run.True(new BundleCache(root, "elsewhere").InspectQuick(large.OrderId, null, null, null, "MED").State != CacheState.CachedValid, "another host misses");

                Promote(cache, large, FidelityLevel.Max, "max");
                run.Equal(Inspect(cache, large, FidelityLevel.Max).State, CacheState.CachedValid, "the fresh download is the slot");
                run.True(Inspect(cache, large, FidelityLevel.Med).State != CacheState.CachedValid, "the old level is gone from it");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        });

        RunNotInBundleCases(run);

        return run.Report("download levels");
    }

    /// <summary>
    /// A promised file the bundle does not hold (a layer the site lacks) is refused by name, and is
    /// not an error: the request goes again naming only the rest. The platform's 404 description of the
    /// download route is the contract.
    /// </summary>
    private static void RunNotInBundleCases(TestRun run)
    {
        run.Case("a refusal naming files the bundle does not hold leaves the rest to ask for, in the order asked", () =>
        {
            string[] asked = ["Elevation/Terrain.dxf", "Basemap/Roads.gpkg", "Site/Site.ifc", "Basemap/Water.gpkg"];
            NotInBundleRetry? retry = DownloadLevels.FilesLeftAfterNotInBundle(404, NotInBundle("Basemap/Water.gpkg", "Basemap/Roads.gpkg"), asked);

            run.True(retry is not null, "the refusal is answered by asking less");
            run.True(retry?.Remaining.SequenceEqual(["Elevation/Terrain.dxf", "Site/Site.ifc"]) ?? false, "the rest, in the order asked");
            run.True(retry?.Absent.SequenceEqual(["Basemap/Water.gpkg", "Basemap/Roads.gpkg"]) ?? false, "and the absent names, as the refusal named them");

            NotInBundleRetry? twice = DownloadLevels.FilesLeftAfterNotInBundle(404, NotInBundle("Basemap/Roads.gpkg", "Basemap/Roads.gpkg"), asked);
            run.Equal(twice?.Absent.Count ?? 0, 1, "a name given twice is absent once");
            run.Equal(twice?.Remaining.Count ?? 0, 3, "and the rest are asked for");
        });

        run.Case("any other refusal is not answered by asking less", () =>
        {
            string[] asked = ["Elevation/Terrain.dxf", "Basemap/Roads.gpkg"];
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(400, NotInBundle("Basemap/Roads.gpkg"), asked) is null, "another status");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, """{ "error": "No such order.", "code": "not_found", "files": ["Basemap/Roads.gpkg"] }""", asked) is null, "another code");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, """{ "error": "No such Extra.", "code": "extra_not_in_bundle", "extra": "brief" }""", asked) is null, "an Extra the bundle does not hold");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, NotInBundle(), asked) is null, "a refusal naming no file");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, """{ "code": "file_not_in_bundle" }""", asked) is null, "a refusal with no file list");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, """{ "code": "file_not_in_bundle", "files": [7] }""", asked) is null, "a name that is not a string");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, NotInBundle("Basemap/Roads.gpkg", "Site/Site.ifc"), asked) is null, "a file that was not asked for");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, NotInBundle("Basemap/Roads.gpkg", "Elevation/Terrain.dxf"), asked) is null, "one that would leave nothing to ask for");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, "<html>not found</html>", asked) is null, "a body that is not JSON");
            run.True(DownloadLevels.FilesLeftAfterNotInBundle(404, NotInBundle("Basemap/Roads.gpkg"), []) is null, "a request that named no file");
        });

        run.Case("a 3.0 download refused for files the bundle does not hold asks again for the rest, and downloads", () =>
        {
            string[] absent = ["Basemap/Roads.gpkg", "Basemap/Water.gpkg", "Basemap/Landuse.gpkg", "Landcover/Landcover.gpkg"];
            using OneFileServer storage = new("level download"u8.ToArray());
            Download download = Downloaded(
                ListedExample("mpb3LargeSite"),
                BundleImportPlanner.ThreeFiles,
                (404, NotInBundle(absent)),
                (200, Link(storage)));

            run.True(download.Result.Ready, $"the download is ready ({download.Result.Message})");
            run.Equal(download.Bodies.Count, 2, "asked twice");
            run.True(Files(download.Bodies[0]).SequenceEqual(BundleImportPlanner.ThreeFiles), "first for every promised file");
            run.True(Files(download.Bodies[1]).SequenceEqual(BundleImportPlanner.ThreeFiles.Except(absent)), "then for only the files left, in the order asked");
            run.Equal((string?)JsonNode.Parse(download.Bodies[1])!["mpb3"]!["level"], "Min", "at the same level");
            run.Equal(storage.Served, 1, "and the link was fetched");
        });

        run.Case("each time the files are asked for again, fewer are named, so the tries end", () =>
        {
            using OneFileServer storage = new("level download"u8.ToArray());
            string[] files = ["Elevation/Terrain.dxf", "Basemap/Roads.gpkg", "Basemap/Water.gpkg"];
            Download download = Downloaded(
                ListedExample("mpb3LargeSite"),
                files,
                (404, NotInBundle("Basemap/Roads.gpkg")),
                (404, NotInBundle("Basemap/Water.gpkg")),
                (200, Link(storage)));

            run.True(download.Result.Ready, $"the download is ready ({download.Result.Message})");
            run.Equal(download.Bodies.Count, 3, "three tries");
            run.True(Files(download.Bodies[1]).SequenceEqual(["Elevation/Terrain.dxf", "Basemap/Water.gpkg"]), "the second names two");
            run.True(Files(download.Bodies[2]).SequenceEqual(["Elevation/Terrain.dxf"]), "the third names one");
        });

        run.Case("any other refusal of a 3.0 download is reported as before, after one request", () =>
        {
            string[] files = ["Elevation/Terrain.dxf", "Basemap/Roads.gpkg"];
            string notFound = """{ "error": "No such order.", "code": "not_found" }""";
            Download refused = Downloaded(ListedExample("mpb3LargeSite"), files, (404, notFound));
            run.False(refused.Result.Ready, "nothing downloaded");
            run.Equal(refused.Bodies.Count, 1, "asked once");
            run.Equal(refused.Result.Message, PlatformErrors.Refusal(404, notFound), "the refusal as the platform worded it");

            string every = NotInBundle(files);
            Download nothingLeft = Downloaded(ListedExample("mpb3LargeSite"), files, (404, every));
            run.False(nothingLeft.Result.Ready, "a bundle holding none of the files downloads nothing");
            run.Equal(nothingLeft.Bodies.Count, 1, "asked once");
            run.Equal(nothingLeft.Result.Message, PlatformErrors.Refusal(404, every), "and says so, as the platform worded it");
        });

        run.Case("a 2.x download is not asked again, whatever its refusal says", () =>
        {
            string body = NotInBundle("Basemap/Roads.gpkg");
            Download two = Downloaded(Two(), ["Elevation/Terrain.dxf", "Basemap/Roads.gpkg"], (404, body));
            run.False(two.Result.Ready, "nothing downloaded");
            run.Equal(two.Bodies.Count, 1, "asked once");
            run.True(SameJson(two.Bodies[0], HostViews.BuildRequestBody()), "with its view request");
            run.Equal(two.Result.Message, PlatformErrors.Refusal(404, body), "and the refusal reported as before");
        });
    }

    /// <summary>The platform's refusal of promised files this bundle does not hold.</summary>
    private static string NotInBundle(params string[] files)
        => new JsonObject
        {
            ["error"] = "This bundle holds no file named " + string.Join(", ", files),
            ["code"] = "file_not_in_bundle",
            ["reason"] = "unknown_file",
            ["files"] = new JsonArray([.. files.Select(file => (JsonNode)file)]),
        }.ToJsonString();

    /// <summary>A level download's link to <paramref name="storage"/>, an hour from expiry.</summary>
    internal static string Link(OneFileServer storage)
        => new JsonObject
        {
            ["url"] = storage.Url,
            ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["sizeBytes"] = storage.Length,
        }.ToJsonString();

    private static IReadOnlyList<string> Files(string body)
        => JsonNode.Parse(body)!["mpb3"]?["files"]?.AsArray().Select(file => (string)file!).ToList() ?? [];

    private readonly record struct Download(CompleteDownload Result, IReadOnlyList<string> Bodies);

    /// <summary>
    /// Downloads <paramref name="bundle"/> through the vault client, the download route answering with
    /// <paramref name="answers"/> in turn; every download request's body is kept.
    /// </summary>
    private static Download Downloaded(VaultBundle bundle, IReadOnlyList<string> files, params (int Status, string Body)[] answers)
    {
        Scripted handler = new(answers);
        VaultClient vault = SignedInVault(handler, files, out AuthSession session);
        string root = Path.Combine(Path.GetTempPath(), "mantleplace-levels-" + Guid.NewGuid().ToString("N"));
        try
        {
            CompleteDownload result = vault.DownloadCompleteAsync(bundle, null, new BundleCache(root), new Progress<string>(), default)
                .GetAwaiter().GetResult();
            return new Download(result, handler.Downloads);
        }
        finally
        {
            session.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>A vault client signed in through <paramref name="handler"/>, naming <paramref name="files"/> in a 3.0 download.</summary>
    internal static VaultClient SignedInVault(HttpMessageHandler handler, IReadOnlyList<string> files, out AuthSession session)
    {
        const string Base = "https://platform.test";
        MantlePlaceEndpoints endpoints = new()
        {
            ApiBaseUrl = Base,
            TokenEndpointUrl = Base + "/api/v1/auth/native/token",
            RefreshEndpointUrl = Base + "/api/v1/auth/native/refresh",
        };
        MemoryStore store = new();
        store.Save("refresh-token", "stored-refresh");
        session = new(endpoints, store, handler);
        session.RestoreAsync(default).GetAwaiter().GetResult();
        return new VaultClient(endpoints, session, handler, files);
    }

    /// <summary>The raw JSON of a 3.0 listing example in the vendored description.</summary>
    internal static string ListedExampleJson(string name)
    {
        using JsonDocument spec = Spec();
        return spec.RootElement.GetProperty("paths").GetProperty("/api/v1/vault/bundles")
            .GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("examples").GetProperty(name).GetProperty("value").GetRawText();
    }

    /// <summary>
    /// Grants a token, answers a route <see cref="Routes"/> names, and answers each download request
    /// with the next scripted answer.
    /// </summary>
    internal sealed class Scripted((int Status, string Body)[] answers) : HttpMessageHandler
    {
        private int _next;

        internal List<string> Downloads { get; } = [];

        /// <summary>The answer for a request's path other than the download route, or <c>null</c> for none.</summary>
        internal Func<string, (int Status, string Body)?> Routes { get; init; } = _ => null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/refresh", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "access_token": "token-1", "refresh_token": "stored-refresh", "expires_in": 3600, "user": { "email": "c@example.com" } }"""),
                };
            }

            if (!path.EndsWith("/download", StringComparison.Ordinal))
            {
                (int routeStatus, string routeBody) = Routes(path) ?? (500, "{}");
                return new HttpResponseMessage((HttpStatusCode)routeStatus) { Content = new StringContent(routeBody) };
            }

            Downloads.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            (int status, string body) = _next < answers.Length ? answers[_next++] : (500, "{}");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        }
    }

    /// <summary>A storage link on loopback that serves one body to every request.</summary>
    internal sealed class OneFileServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _body;
        private int _served;

        internal OneFileServer(byte[] body)
        {
            _body = body;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/level.zip";
            _ = Task.Run(ServeAsync);
        }

        internal string Url { get; }

        internal long Length => _body.Length;

        internal int Served => Volatile.Read(ref _served);

        public void Dispose() => _listener.Stop();

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using System.Net.Sockets.TcpClient client = await _listener.AcceptTcpClientAsync();
                    using System.Net.Sockets.NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[4096];
                    string seen = string.Empty;
                    int read;
                    while (!seen.Contains("\r\n\r\n", StringComparison.Ordinal) && (read = await stream.ReadAsync(buffer)) > 0)
                    {
                        seen += System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                    }

                    Interlocked.Increment(ref _served);
                    byte[] head = System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(_body);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException or IOException)
            {
                // Stopped: the test is done with the link.
            }
        }
    }

    /// <summary>Every promised file a 3.0 download may name, from the vendored Platform API description.</summary>
    internal static IReadOnlyList<string> PromisedFiles()
    {
        using JsonDocument spec = Spec();
        JsonElement mpb3 = spec.RootElement.GetProperty("paths").GetProperty("/api/v1/vault/bundles/{orderId}/download")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("properties").GetProperty("mpb3");
        JsonElement level = mpb3.GetProperty("anyOf").EnumerateArray()
            .First(form => form.GetProperty("properties").TryGetProperty("files", out _));
        return [.. level.GetProperty("properties").GetProperty("files").GetProperty("items").GetProperty("enum")
            .EnumerateArray().Select(file => file.GetString()!)];
    }

    /// <summary>Holds a host's declared 3.0 files to the published promise: each one promised, none twice.</summary>
    internal static void AssertPromised(TestRun run, IReadOnlyList<string> files, string host)
    {
        IReadOnlyList<string> promised = PromisedFiles();
        run.True(files.Count > 0, host + " names its files");
        foreach (string file in files)
        {
            run.True(promised.Contains(file), host + " reads " + file + ", which the description promises");
        }

        run.Equal(files.Distinct(StringComparer.Ordinal).Count(), files.Count, host + " names each file once");
    }

    /// <summary>One bundle from a 3.0 listing example in the vendored description.</summary>
    internal static VaultBundle ListedExample(string name)
    {
        using JsonDocument spec = Spec();
        JsonElement example = spec.RootElement.GetProperty("paths").GetProperty("/api/v1/vault/bundles")
            .GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("examples").GetProperty(name).GetProperty("value");
        string? error = VaultListingReader.TryParse(example.GetRawText(), out VaultListing listing);
        if (error is not null || listing.Bundles.Count != 1)
        {
            throw new InvalidOperationException("The listing example " + name + " did not read: " + error);
        }

        return listing.Bundles[0];
    }

    private static JsonDocument Spec()
    {
        string generated = RepoTree.Find("shared/src/MantlePlace.PlatformApi/Generated")
            ?? throw new InvalidOperationException("The vendored Platform API description was not found.");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(generated, "openapi.json")));
    }

    private static VaultBundle Two() => new() { OrderId = "two", ManifestVersion = "2.4.0" };

    private static CacheEntry Inspect(BundleCache cache, VaultBundle bundle, FidelityLevel level)
        => cache.InspectQuick(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion, DownloadLevels.SlotLevel(bundle, level));

    private static void Promote(BundleCache cache, VaultBundle bundle, FidelityLevel level, string body)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        string? error = cache.PromoteAsync(
            bundle.OrderId,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(),
            bytes.Length,
            expectedSha256: null,
            bundle.ManifestVersion,
            DateTimeOffset.UtcNow,
            default,
            holdsView: true,
            level: DownloadLevels.SlotLevel(bundle, level)).GetAwaiter().GetResult();
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>The body the vault client sends for a download of <paramref name="bundle"/>, refused so nothing is fetched.</summary>
    private static string SentBody(VaultBundle bundle, FidelityLevel? level, IReadOnlyList<string> files)
    {
        const string Base = "https://platform.test";
        Recorder handler = new();
        MantlePlaceEndpoints endpoints = new()
        {
            ApiBaseUrl = Base,
            TokenEndpointUrl = Base + "/api/v1/auth/native/token",
            RefreshEndpointUrl = Base + "/api/v1/auth/native/refresh",
        };
        MemoryStore store = new();
        store.Save("refresh-token", "stored-refresh");
        using AuthSession session = new(endpoints, store, handler);
        session.RestoreAsync(default).GetAwaiter().GetResult();
        VaultClient vault = new(endpoints, session, handler, files);
        string root = Path.Combine(Path.GetTempPath(), "mantleplace-levels-" + Guid.NewGuid().ToString("N"));
        try
        {
            vault.DownloadCompleteAsync(bundle, null, new BundleCache(root), new Progress<string>(), default, level)
                .GetAwaiter().GetResult();
            return handler.Bodies.Last();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool SameJson(string actual, string expected)
        => JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected));

    private sealed class MemoryStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = [];

        public bool IsPersistent => true;

        public bool Save(string key, string secret)
        {
            _secrets[key] = secret;
            return true;
        }

        public bool TryLoad(string key, out string secret) => _secrets.TryGetValue(key, out secret!);

        public void Clear(string key) => _secrets.Remove(key);
    }

    /// <summary>Grants a token, then refuses every call, recording each body.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return request.RequestUri!.AbsolutePath.EndsWith("/refresh", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "access_token": "token-1", "refresh_token": "stored-refresh", "expires_in": 3600, "user": { "email": "c@example.com" } }"""),
                }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
        }
    }
}
