namespace MantlePlace.Revit.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

/// <summary>The fidelity-level expectations: <c>revitLevels</c> rows and <c>revitLevelsAbsent</c> entries.</summary>
internal static partial class ManifestConformanceTests
{
    /// <summary>
    /// Asserts each <c>revitLevels</c> row against the level the reader produced, and each
    /// <c>revitLevelsAbsent</c> entry as publishing no levels.
    /// </summary>
    /// <remarks>
    /// A row states only what it pins: the kind always, and a path, hash, row count, field, target,
    /// reason or cost where the case is about one. A key the row carries is compared even when its
    /// value is <c>null</c> — <c>reason: null</c> is the unknown reason, read as none, and
    /// <c>costUnit: null</c> the unknown unit, read as unknown — so neither can be skipped for free.
    /// </remarks>
    private static void AssertRevitLevels(TestRun run, CorpusCase corpusCase, BundleManifest manifest)
    {
        if (ConformanceCorpus.WantsRows(corpusCase, "revitLevels", out IReadOnlyList<ExpectationNode> rows))
        {
            int index = 0;
            foreach (ExpectationNode row in rows)
            {
                AssertLevelRow(run, manifest, row, $"revitLevels[{index}]");
                index++;
            }
        }

        if (ConformanceCorpus.WantsRows(corpusCase, "revitLevelsAbsent", out IReadOnlyList<ExpectationNode> absent))
        {
            foreach (ExpectationNode node in absent)
            {
                string? entry = node.AsString();
                BundleArtifact? artifact = LevelEntry(manifest, entry);
                run.True(artifact is not null, $"revitLevelsAbsent: '{entry}' is an entry the reader produced");
                run.True(artifact?.Levels is null, $"revitLevelsAbsent: '{entry}' publishes no levels, MAX only");
            }
        }
    }

    private static void AssertLevelRow(TestRun run, BundleManifest manifest, ExpectationNode row, string where)
    {
        string? entry = row.Str("entry");
        string? token = row.Str("level");
        string? kind = row.Str("kind");

        FidelityLevels? levels = LevelEntry(manifest, entry)?.Levels;
        run.True(levels is not null, $"{where}: '{entry}' publishes levels");
        if (levels is null || !FidelityLevelNames.TryParse(token, out FidelityLevel level))
        {
            run.True(levels is null, $"{where}: '{token}' is a level");
            return;
        }

        PublishedLevel published = levels[level];
        run.Equal(KindToken(published.Kind), kind, $"{where}.kind");

        if (Has(row, "path"))
        {
            run.Equal(published.File?.Path, row.Str("path"), $"{where}.path");
        }

        if (Has(row, "sha256"))
        {
            run.Equal(published.File?.Sha256, row.Str("sha256"), $"{where}.sha256");
        }

        if (Has(row, "rows"))
        {
            run.Equal(published.Rows ?? -1, row.Int("rows") ?? -2, $"{where}.rows");
        }

        if (Has(row, "lowestLevelField"))
        {
            run.Equal(published.LowestLevelField, row.Str("lowestLevelField"), $"{where}.lowestLevelField");
        }

        if (Has(row, "sameAs"))
        {
            run.Equal(
                published.SameAs is { } target ? FidelityLevelNames.Token(target) : null,
                row.Str("sameAs"),
                $"{where}.sameAs");
        }

        if (Has(row, "reason"))
        {
            run.Equal(ReasonToken(published.Reason), row.Str("reason"), $"{where}.reason");
        }

        if (Has(row, "costUnit"))
        {
            run.True(published.Cost is not null, $"{where}: a cost driver is read");
            run.Equal(
                published.Cost is { Unit: not CostUnit.Unknown } cost ? cost.RawUnit : null,
                row.Str("costUnit"),
                $"{where}.costUnit");
        }

        if (Has(row, "costCount"))
        {
            run.Equal((int)(published.Cost?.Count ?? -1), row.Int("costCount") ?? -2, $"{where}.costCount");
        }
    }

    private static bool Has(ExpectationNode row, string key) => row.Element.TryGetProperty(key, out _);

    /// <summary>The entry a row names, by its key in the manifest — this host's own block first, as the reader resolves it.</summary>
    private static BundleArtifact? LevelEntry(BundleManifest manifest, string? entry) => entry switch
    {
        "road_splines" => manifest.RoadSplines,
        "land_use" => manifest.LandUse,
        "land_cover" => manifest.LandCover,
        "water" => manifest.Water,
        "road_polygons" => manifest.RoadPolygons,
        "flood_zones" => manifest.FloodZones,
        "steep_slope" => manifest.SteepGround,
        "drape" => manifest.RevitDrape,
        "contours" => manifest.RevitContours,
        "tree_points" => manifest.TreePoints,
        _ => null,
    };

    private static string KindToken(FidelityLevelKind kind) => kind switch
    {
        FidelityLevelKind.Max => "max",
        FidelityLevelKind.Pointer => "pointer",
        FidelityLevelKind.RowCut => "rowCut",
        FidelityLevelKind.FieldCut => "fieldCut",
        FidelityLevelKind.SameAs => "sameAs",
        FidelityLevelKind.Unavailable => "unavailable",
        _ => kind.ToString(),
    };

    private static string? ReasonToken(SameAsReason? reason) => reason switch
    {
        SameAsReason.MaxOnly => "max_only",
        SameAsReason.NotDerived => "not_derived",
        SameAsReason.CapNotReached => "cap_not_reached",
        SameAsReason.NoReduction => "no_reduction",
        SameAsReason.SmallestValidSize => "smallest_valid_size",
        null => null,
        _ => reason.ToString(),
    };
}
