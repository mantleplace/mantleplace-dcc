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
            run.Equal(offer.Recommended, FidelityLevel.Med, "Med is recommended for the large site");
            run.Equal(offer.Levels.Count, 3, "three levels");
            run.True(offer.Levels.Select(level => level.Level).SequenceEqual(DownloadLevels.Offered), "Max, Med, Min in order");
            run.True(offer.SizeOf(FidelityLevel.Max).SizeBytes == 1811415040L, "Max's size");
            run.True(offer.SizeOf(FidelityLevel.Min).SizeBytes == 1227463066L, "Min's size");
            run.True(offer.SizeOf(FidelityLevel.Med).Triangles == 48000000L, "Med's counts");
            run.Equal(offer.Extras.Count, 3, "three Extras");
            run.Equal(offer.Extras[0].Name, "Site Brief", "named as a curator sees them");

            run.Equal(ListedExample("mpb3SmallSite").Levels!.Recommended, FidelityLevel.Max, "Max for the small site");
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
            run.Equal(control.Options[1].Label, "Med — 1.4 GB (recommended)", "Med with its size, marked as the recommendation");
            run.Equal(control.Options[2].Label, "Min — 1.2 GB", "Min with its size");
            run.True(control.Options[1].IsRecommended && !control.Options[0].IsRecommended, "one recommendation");
            run.Equal(control.Options[1].ToolTip, "Med: 700,000 rows, 212,000 features, 48,000,000 triangles, 16,777,216 pixels", "the counts behind the size");
            run.Equal(control.Selected, FidelityLevel.Med, "preselected on the recommendation");
            run.Equal(control.SelectedIndex, 1, "the control shows it");

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
            first.Choose(FidelityLevel.Min);

            run.Equal(LevelControl.For(large)!.Selected, FidelityLevel.Med, "built again for the export, it is on the recommendation");
            run.Equal(DownloadLevels.SlotLevel(large), "MED", "the export's cache slot is the recommendation's");
            run.Equal(DownloadLevels.SlotLevel(Two()), HostViews.EveryEntryLevel, "a 2.x order's slot is the one it always was");
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

        run.Case("Revit's 3.0 files are files the published description promises", () =>
            AssertPromised(run, BundleImportPlanner.ThreeFiles, "Revit"));

        run.Case("the vault client asks for a 3.0 order at the chosen level, or the recommendation; a 2.x order as before", () =>
        {
            string[] files = ["Elevation/Contours.dxf"];
            VaultBundle large = ListedExample("mpb3LargeSite");

            run.True(SameJson(SentBody(large, FidelityLevel.Min, files), DownloadLevels.BuildRequestBody(FidelityLevel.Min, files)), "the chosen level and the host's files");
            run.True(SameJson(SentBody(large, null, files), DownloadLevels.BuildRequestBody(FidelityLevel.Med, files)), "no level is the recommendation");
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

        return run.Report("download levels");
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
    private static VaultBundle ListedExample(string name)
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
