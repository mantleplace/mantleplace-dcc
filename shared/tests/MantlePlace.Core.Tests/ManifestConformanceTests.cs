using System.Text.Json;

namespace MantlePlace.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

internal static partial class ManifestConformanceTests
{
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
        "revitRoadSplinesPath",
        "revitWaterPath",
        "revitRoadSplinesSha256",
        "revitRoadSplinesTable",
        "revitRoadSplinesFallbackPath",
        "revitDrapePath",
        "revitContoursPath",
        "revitContoursVerticalUnits",
        "revitLevels",
        "revitLevelsAbsent",
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

        run.Case("every version floor parses, and an unparseable one refuses loudly instead of disabling the gate", () =>
        {
            foreach (string floor in new[] { ManifestVersions.MinSupportedManifestVersion, HeightDatums.StatedFromVersion })
            {
                run.True(ManifestVersion.Parse(floor).IsValid, $"{floor} is a MAJOR.MINOR.PATCH version");
            }

            bool refused = false;
            try
            {
                ManifestVersion.IsBelowFloor("1.0.0", "1.0");
            }
            catch (ArgumentException)
            {
                refused = true;
            }

            run.True(refused, "a typo in a floor throws rather than letting every version through");
        });

        run.Case("corpus pin is at or above this host's floor (clean break, HPS-31)", () =>
            run.True(
                !ManifestVersion.IsBelowFloor(
                    ConformanceCorpus.PinnedManifestVersion(), ManifestVersions.MinSupportedManifestVersion),
                $"corpus manifestVersion {ConformanceCorpus.PinnedManifestVersion()} "
                + $"< MinSupportedManifestVersion {ManifestVersions.MinSupportedManifestVersion}"));

        foreach (CorpusCase corpusCase in cases)
        {
            run.Case(corpusCase.Id, () => DriveCase(run, corpusCase));
        }

        RunHostLocalAssertions(run, cases);

        return run.Report("manifest conformance");
    }

    internal static void DriveCase(TestRun run, CorpusCase corpusCase)
    {
        if (string.Equals(corpusCase.Id, "manifest.materializationSignals", StringComparison.Ordinal))
        {
            DriveMaterializationSignals(run, corpusCase);
            return;
        }

        if (string.Equals(corpusCase.Id, "manifest.foliageTypeUnknownValue", StringComparison.Ordinal))
        {
            DriveFoliageTypeVectors(run, corpusCase);
            return;
        }

        if (string.Equals(corpusCase.Id, "manifest.revitHeightDatum", StringComparison.Ordinal))
        {
            DriveHeightDatumVectors(run, corpusCase);
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

        AssertExpectations(run, corpusCase, manifest);

        foreach (string problem in ConformanceCorpus.UnassertedExpectations(corpusCase, ConsumedExpectationKeys))
        {
            run.Fail($"{problem} (HPS-46).");
        }

        foreach (string problem in ConformanceCorpus.UnassertedNestedExpectations(corpusCase))
        {
            run.Fail(problem);
        }
    }

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

    private static readonly SiteFrame FoliageFrame = new()
    {
        Origin = new GeoOrigin
        {
            Epsg = 32613,
            Easting = 441859.50,
            Northing = 4014272.50,
            LinearUnit = LinearUnit.Metre,
        },
    };

    private static void DriveFoliageTypeVectors(TestRun run, CorpusCase corpusCase)
    {
        using VectorDocument vectors = VectorDocument.Parse(corpusCase.Payload);
        IReadOnlyList<VectorNode> rows = vectors.Root.Items("rows");
        if (rows.Count == 0)
        {
            run.Fail("the vector file has no non-empty `rows` array — zero rows would report "
                + "green for the wrong reason (HPS-40)");
            return;
        }

        foreach (VectorNode row in rows)
        {
            string name = row.Str("name") ?? string.Empty;
            string? csv = row.Str("csv");
            IReadOnlyList<VectorNode> expected = row.Items("foliageTypes");

            if (name.Length == 0 || csv is null || expected.Count == 0)
            {
                run.Fail($"row '{name}' is missing `name`, a `csv` string, or a `foliageTypes` array");
                continue;
            }

            string? vocabulary = row.Str("foliageTypeVocabulary");

            TreePointsParse parse = TreePointsReader.Parse(csv, FoliageFrame, vocabulary, groundUnit: LinearUnit.Metre);
            run.True(parse.Failure is null, $"row '{name}': parsed ({parse.Failure})");
            run.Equal(parse.Points.Count, expected.Count, $"row '{name}': a point per published row");

            for (int index = 0; index < expected.Count && index < parse.Points.Count; index++)
            {
                run.Equal(
                    Spell(parse.Points[index].FoliageType),
                    expected[index].AsString(),
                    $"row '{name}', point {index + 1}");
            }
        }

        foreach (string unread in vectors.UnreadPaths())
        {
            run.Fail($"'{unread}' is a vector value nothing in this suite read.");
        }
    }

    private static void DriveHeightDatumVectors(TestRun run, CorpusCase corpusCase)
    {
        using VectorDocument vectors = VectorDocument.Parse(corpusCase.Payload);
        if (vectors.Root.Obj("manifest") is not { } manifestNode)
        {
            run.Fail("the vector file has no embedded `manifest` object");
            return;
        }

        string baseManifest = manifestNode.Element.GetRawText();
        vectors.Root.MarkConsumed("manifest");

        IReadOnlyList<VectorNode> rows = vectors.Root.Items("rows");
        if (rows.Count == 0)
        {
            run.Fail("the vector file has no non-empty `rows` array — zero rows would report "
                + "green for the wrong reason (HPS-40)");
            return;
        }

        foreach (VectorNode row in rows)
        {
            string name = row.Str("name") ?? string.Empty;
            string? outcome = row.Str("outcome");
            VectorNode? ground = row.Obj("ground");
            if (name.Length == 0 || outcome is not ("placeable" or "unplaceable") || ground is null)
            {
                run.Fail($"row '{name}' is missing `name`, a `ground` object, or an `outcome` of "
                    + "`placeable` or `unplaceable`");
                continue;
            }

            System.Text.Json.Nodes.JsonObject document = System.Text.Json.Nodes.JsonNode.Parse(baseManifest)!.AsObject();
            Overlay(document, row, "root");
            Overlay(document["landcover"]!["tree_points"]!.AsObject(), row, "treePoints");

            BundleManifest manifest = BundleManifestReader.Parse(document.ToJsonString());
            if (!manifest.IsValid || manifest.TreePoints is not { } treePoints)
            {
                run.Fail($"row '{name}': the manifest must parse and carry tree points ({manifest.Error})");
                continue;
            }

            BundleImportPlan plan = BundleImportPlanner.Plan(manifest, [treePoints.Path], _ => null);
            if (plan.Steps.FirstOrDefault(step => step.Kind == ImportStepKind.Vegetation) is not { HeightDatum: { } content })
            {
                run.Fail($"row '{name}': the tree step was not planned with a height datum");
                continue;
            }

            string? refusal = HeightDatums.Refusal(
                new GroundDatum(1, ground.Str("vertical_datum")),
                content,
                HeightDatums.Noun(ImportStepKind.Vegetation));

            if (outcome == "placeable")
            {
                run.True(refusal is null, $"row '{name}': placeable (refused with: {refusal})");
            }
            else
            {
                run.True(refusal is not null, $"row '{name}': unplaceable");
                run.Contains(refusal, row.Str("errorContains") ?? string.Empty, $"row '{name}': the refusal names it");
            }
        }

        foreach (string unread in vectors.UnreadPaths())
        {
            run.Fail($"'{unread}' is a vector value nothing in this suite read.");
        }
    }

    private static void Overlay(System.Text.Json.Nodes.JsonObject target, VectorNode row, string key)
    {
        if (row.Obj(key) is not { } overlay)
        {
            return;
        }

        foreach (JsonProperty member in overlay.Element.EnumerateObject())
        {
            if (member.Value.ValueKind == JsonValueKind.Null)
            {
                target.Remove(member.Name);
            }
            else
            {
                target[member.Name] = System.Text.Json.Nodes.JsonNode.Parse(member.Value.GetRawText());
            }
        }

        row.MarkConsumed(key);
    }

    private static string Spell(FoliageType foliage) => foliage switch
    {
        FoliageType.Shrub => "shrub",
        _ => "tree",
    };

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

        if (ConformanceCorpus.WantsString(corpusCase, "revitRoadSplinesPath", out string revitRoadSplinesPath))
        {
            run.Equal(manifest.RoadSplines?.Path ?? string.Empty, revitRoadSplinesPath, "revitRoadSplinesPath");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "revitWaterPath", out string revitWaterPath))
        {
            run.Equal(manifest.Water?.Path ?? string.Empty, revitWaterPath, "revitWaterPath");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "revitRoadSplinesSha256", out string revitRoadSplinesSha))
        {
            run.Equal(manifest.RoadSplines?.Sha256 ?? string.Empty, revitRoadSplinesSha, "revitRoadSplinesSha256");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "revitRoadSplinesTable", out string revitRoadSplinesTable))
        {
            run.Equal(manifest.RoadSplines?.Table ?? string.Empty, revitRoadSplinesTable, "revitRoadSplinesTable");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "revitRoadSplinesFallbackPath", out string revitRoadSplinesFallbackPath))
        {
            run.Equal(
                manifest.RoadSplines?.GeoJsonFallback?.Path ?? string.Empty,
                revitRoadSplinesFallbackPath,
                "revitRoadSplinesFallbackPath");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "revitDrapePath", out string revitDrapePath))
        {
            run.Equal(manifest.RevitDrape?.Path ?? string.Empty, revitDrapePath, "revitDrapePath");
        }

        AssertRevitLevels(run, corpusCase, manifest);

        if (ConformanceCorpus.WantsString(corpusCase, "revitContoursPath", out string revitContoursPath))
        {
            run.Equal(manifest.RevitContours?.Path ?? string.Empty, revitContoursPath, "revitContoursPath");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "revitContoursVerticalUnits", out string revitContoursVerticalUnits))
        {
            run.Equal(manifest.RevitContours?.VerticalUnits ?? string.Empty, revitContoursVerticalUnits, "revitContoursVerticalUnits");
        }

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
            run.Equal(manifest.Version, "", "an integer-era version is not readable as a string");
        });

        run.Case("an unsupported delivery enum fails closed and names the value (HPS-35)", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(
                "{\"version\": \"1.0.0\", \"hosts\": {\"unreal\": {}}, \"delivery\": {\"linear_unit\": \"furlong\"}}");
            run.False(manifest.IsValid, "unknown linear_unit is refused");
            run.Contains(manifest.Error, "furlong", "refusal names the offending value");
        });
    }
}
