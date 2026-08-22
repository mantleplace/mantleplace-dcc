using System.Text.Json;

namespace MantlePlace.Revit.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

/// <summary>
/// Drives the shared corpus' <c>manifest</c> group against <see cref="BundleManifestReader"/>.
/// </summary>
/// <remarks>
/// The suite iterates <c>index.json</c> at run time rather than transcribing vectors into C#
/// literals (HPS-40). Transcribed vectors are how two hosts come to disagree about one contract
/// while both suites stay green — editing a case here must turn this red, and it does.
/// </remarks>
internal static class ManifestConformanceTests
{
    /// <summary>
    /// Expectation keys this host knows how to assert. The list no longer proves coverage by
    /// itself — the per-case <c>AssertedKeys</c> tracking does (HPS-46) — it is kept to tell
    /// "unknown key" apart from "known key declared with a type this host could not read" in the
    /// failure message. Internal so the self-test suite can prove the reserved
    /// <c>selfTest</c>-prefixed keys are reported against the REAL list, not a stand-in.
    /// </summary>
    internal static readonly HashSet<string> ConsumedExpectationKeys =
    [
        "jobId",
        "outcome",
        "tokens",
        "orderId",
        "deliveryModel",
        "cesiumTerrainPath",
        "cesiumTerrainPrefix",
        "roadSplinesPath",
        "roadSplinesSha256",
        "hasRoadSplines",
        "toposurfacePointsSha256",
        "surfaceDxfSha256",
        "ifcSiteSha256",
        "surveyPointEpsg",
        "surveyPointEasting",
        "surveyPointNorthing",
        "surveyPointLinearUnit",
        "gridRotationDeg",
    ];

    internal static int Run()
    {
        TestRun run = new();

        if (ConformanceCorpus.LoadGroup("manifest", out List<CorpusCase> cases) is { } loadError)
        {
            run.Case("corpus loads", () => run.Fail($"conformance corpus unusable: {loadError}"));
            return run.Report("manifest conformance");
        }

        // The corpus is pinned at one manifest version; this parser has its own floor. Equality
        // was the rule while every host repinned together. Hosts repin INDEPENDENTLY now --
        // the corpus tracks the newest published contract, while a host that has not yet taken the
        // clean break still floors lower and accepts the newer shape (the readers gate `<`, not
        // `!=`). What must never happen is the corpus falling BELOW this floor: then every accept
        // case below is a document this parser refuses, and a dozen derived assertions fail with
        // confusing messages instead of this one saying it once, loudly.
        run.Case("corpus pin is at or above this host's floor (clean break, HPS-31)", () =>
            run.True(
                ConformanceCorpus.PinnedManifestVersion() >= ManifestVersions.MinSupportedManifestVersion,
                $"corpus manifestVersion {ConformanceCorpus.PinnedManifestVersion()} "
                + $"< MinSupportedManifestVersion {ManifestVersions.MinSupportedManifestVersion}"));

        // Every case goes through DriveCase in one loop, so coverage is structural: a case added
        // to the corpus is driven the next time this runs, with no edit here, and a case DriveCase
        // has not been taught fails on its unsupported `expect` rather than being skipped. That is
        // why there is no UndrivenCases check — it could never report anything, and a check that
        // cannot fail reads as assurance it does not provide. A group whose cases each drive a
        // DIFFERENT entry point (vault, auth) needs one; this group does not.
        foreach (CorpusCase corpusCase in cases)
        {
            run.Case(corpusCase.Id, () => DriveCase(run, corpusCase));
        }

        RunHostLocalAssertions(run, cases);

        return run.Report("manifest conformance");
    }

    private static void DriveCase(TestRun run, CorpusCase corpusCase)
    {
        // The group's one id-dispatched case: its `expect: "vector"` rows drive the
        // materialization decision (HPS-47) rather than the whole parser. A second vector case
        // added to the corpus lands in the unsupported-expect failure below until taught, so the
        // dispatch cannot silently skip it.
        if (string.Equals(corpusCase.Id, "manifest.materializationSignals", StringComparison.Ordinal))
        {
            DriveMaterializationSignals(run, corpusCase);
            return;
        }

        BundleManifest manifest = BundleManifestReader.Parse(corpusCase.Payload);

        if (corpusCase.IsAccept)
        {
            run.True(manifest.IsValid, $"accepted (rejected with: {manifest.Error})");
        }
        else if (corpusCase.IsReject)
        {
            run.False(manifest.IsValid, "rejected");
            run.True(manifest.Error.Length > 0, "rejection states a reason");
        }
        else
        {
            run.Fail($"unsupported expect '{corpusCase.Expect}' in the manifest group");
            return;
        }

        if (corpusCase.ErrorContains.Length > 0)
        {
            run.Contains(manifest.Error, corpusCase.ErrorContains, "message");
        }

        // Expectations are asserted on reject cases too: the corpus pins values the parser must
        // have read BEFORE refusing, which is how the vault join key survives an unimportable
        // bundle (HPS-37).
        AssertExpectations(run, corpusCase, manifest);

        foreach (string problem in ConformanceCorpus.UnassertedExpectations(corpusCase, ConsumedExpectationKeys))
        {
            run.Fail($"{problem} (HPS-46).");
        }
    }

    /// <summary>
    /// Drives <c>manifest.materializationSignals</c> (HPS-47): each row is a complete v18 manifest
    /// with an expected materialization verdict. Rows go through the public
    /// <see cref="BundleManifestReader.Parse"/> seam rather than an internal entry point — this
    /// host's materialization decision IS whether the not-materialized refusal fires.
    /// </summary>
    private static void DriveMaterializationSignals(TestRun run, CorpusCase corpusCase)
    {
        using JsonDocument document = JsonDocument.Parse(corpusCase.Payload);
        if (!document.RootElement.TryGetProperty("vectors", out JsonElement rows)
            || rows.ValueKind != JsonValueKind.Array
            || rows.GetArrayLength() == 0)
        {
            run.Fail("the vector file has no non-empty `vectors` array — zero rows would report "
                + "green for the wrong reason (HPS-40)");
            return;
        }

        foreach (JsonElement row in rows.EnumerateArray())
        {
            string name = row.ValueKind == JsonValueKind.Object
                && row.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;

            if (name.Length == 0
                || !row.TryGetProperty("manifest", out JsonElement embedded)
                || embedded.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("materialized", out JsonElement verdict)
                || verdict.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                run.Fail($"row '{name}' is missing `name`, an embedded `manifest` object, or a "
                    + "boolean `materialized`");
                continue;
            }

            BundleManifest manifest = BundleManifestReader.Parse(embedded.GetRawText());
            if (verdict.GetBoolean())
            {
                run.True(manifest.IsValid, $"row '{name}': materialized, so no not-materialized "
                    + $"refusal (rejected with: {manifest.Error})");
            }
            else
            {
                run.False(manifest.IsValid, $"row '{name}': not materialized, so refused");
                run.Contains(
                    manifest.Error,
                    "hasn't generated its DCC formats",
                    $"row '{name}': the refusal that fired is the not-materialized one");
            }
        }
    }

    private static void AssertExpectations(TestRun run, CorpusCase corpusCase, BundleManifest manifest)
    {
        if (ConformanceCorpus.WantsString(corpusCase, "jobId", out string jobId))
        {
            run.Equal(manifest.JobId, jobId, "jobId");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "orderId", out string orderId))
        {
            run.Equal(manifest.OrderId, orderId, "orderId");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "deliveryModel", out string deliveryModel))
        {
            run.Equal(manifest.DeliveryModel, deliveryModel, "deliveryModel");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "cesiumTerrainPath", out string cesiumTerrainPath))
        {
            run.Equal(manifest.CesiumTerrainPath, cesiumTerrainPath, "cesiumTerrainPath");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "cesiumTerrainPrefix", out string cesiumTerrainPrefix))
        {
            run.Equal(
                BundleManifest.DeriveCesiumTerrainPrefix(manifest.CesiumTerrainPath),
                cesiumTerrainPrefix,
                "cesiumTerrainPrefix");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "roadSplinesPath", out string roadSplinesPath))
        {
            run.Equal(manifest.RoadSplinesPath, roadSplinesPath, "roadSplinesPath");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "roadSplinesSha256", out string roadSplinesSha))
        {
            run.Equal(manifest.RoadSplinesSha256, roadSplinesSha, "roadSplinesSha256");
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "hasRoadSplines", out bool hasRoadSplines))
        {
            run.Equal(manifest.HasRoadSplines, hasRoadSplines, "hasRoadSplines");
        }

        // The three v19 `revit.*` hashes. Asserting the VALUE rather than a has-hash flag is what
        // binds each one to its own artifact: a reader that resolved the block once and reused the
        // hash would pass a flag check and fail here (HPS-34).
        if (ConformanceCorpus.WantsString(corpusCase, "toposurfacePointsSha256", out string pointsSha))
        {
            run.Equal(manifest.ToposurfacePoints?.Sha256, pointsSha, "toposurfacePointsSha256");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "surfaceDxfSha256", out string surfaceSha))
        {
            run.Equal(manifest.SurfaceDxf?.Sha256, surfaceSha, "surfaceDxfSha256");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "ifcSiteSha256", out string ifcSha))
        {
            run.Equal(manifest.SiteIfc?.Sha256, ifcSha, "ifcSiteSha256");
        }

        // The survey point, asserted through the RESOLVED value rather than through the block it
        // came from. That is the whole point of the case: several origins are published and only
        // one is this host's, so a reader that merged them, or that took whichever it found first,
        // reports a different EPSG here while every other assertion stays green (HPS-33).
        if (ConformanceCorpus.WantsInt(corpusCase, "surveyPointEpsg", out int surveyEpsg))
        {
            run.Equal(manifest.SurveyPoint?.Epsg ?? 0, surveyEpsg, "surveyPointEpsg");
        }

        if (ConformanceCorpus.WantsDouble(corpusCase, "surveyPointEasting", out double surveyEasting))
        {
            run.Within(manifest.SurveyPoint?.Easting ?? double.NaN, surveyEasting, 1e-9, "surveyPointEasting");
        }

        if (ConformanceCorpus.WantsDouble(corpusCase, "surveyPointNorthing", out double surveyNorthing))
        {
            run.Within(manifest.SurveyPoint?.Northing ?? double.NaN, surveyNorthing, 1e-9, "surveyPointNorthing");
        }

        // The origin's unit is NOT the artifacts' unit — they differ on the foot tiers, and reading
        // a State-Plane-foot origin as metres places the site 3.28× out.
        if (ConformanceCorpus.WantsString(corpusCase, "surveyPointLinearUnit", out string surveyUnit))
        {
            run.Equal(
                LinearUnits.ToManifestToken(manifest.SurveyPoint?.LinearUnit ?? LinearUnit.Unspecified),
                surveyUnit,
                "surveyPointLinearUnit");
        }

        if (ConformanceCorpus.WantsDouble(corpusCase, "gridRotationDeg", out double gridRotation))
        {
            run.Within(
                manifest.Georeference.GridRotationDeg ?? double.NaN,
                gridRotation,
                1e-12,
                "gridRotationDeg");
        }
    }

    /// <summary>
    /// Assertions that are this host's own rather than the contract's, and so are deliberately not
    /// corpus cases (DOC-06): the derived-prefix helper's edge cases, and the wording of the
    /// refusal a user actually reads.
    /// </summary>
    private static void RunHostLocalAssertions(TestRun run, List<CorpusCase> cases)
    {
        run.Case("DeriveCesiumTerrainPrefix edge cases", () =>
        {
            run.Equal(BundleManifest.DeriveCesiumTerrainPrefix(string.Empty), string.Empty, "empty path");
            run.Equal(BundleManifest.DeriveCesiumTerrainPrefix("layer.json"), string.Empty, "bare file, not \"/\"");
            run.Equal(
                BundleManifest.DeriveCesiumTerrainPrefix("Elevation/Terrain/layer.json"),
                "Elevation/Terrain/",
                "nested path");
        });

        run.Case("the base-on-demand refusal tells the user what to do", () =>
        {
            CorpusCase? baseOnDemand = cases.Find(c =>
                string.Equals(c.Id, "manifest.baseOnDemand", StringComparison.Ordinal));
            if (baseOnDemand is null)
            {
                run.Fail("corpus case manifest.baseOnDemand has gone missing");
                return;
            }

            BundleManifest manifest = BundleManifestReader.Parse(baseOnDemand.Payload);
            run.Contains(manifest.Error, "mantle.place/vault", "refusal names the vault");
            run.Contains(manifest.Error, "re-download", "refusal names the remedy");
        });

        run.Case("the version-gate refusal names re-procurement, not dual-parsing", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse("{\"version\": 17, \"unreal\": {}}");
            run.False(manifest.IsValid, "v17 is refused");
            run.Contains(manifest.Error, "no longer supported", "names the version gate");
            run.Contains(manifest.Error, "mantle.place/vault", "names the vault");
            run.Equal(manifest.Version, 17, "version was still read");
        });

        run.Case("an unsupported delivery enum fails closed and names the value (HPS-35)", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                "{\"version\": 18, \"unreal\": {}, \"delivery\": {\"linear_unit\": \"furlong\"}}");
            run.False(manifest.IsValid, "unknown linear_unit is refused");
            run.Contains(manifest.Error, "furlong", "refusal names the offending value");
        });
    }
}
