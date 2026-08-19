using System.Text.Json;

namespace MantlePlace.Revit.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

/// <summary>
/// Runs this host's corpus reader over <c>tools/manifest-conformance/corpus/self-test/</c> — the
/// deliberately broken index and case fixtures every host reader must REJECT (HPS-46). The
/// inversion is the whole point: a fixture the reader ACCEPTS fails this suite, because a reader
/// that tolerates a broken corpus is how a conformance suite comes to report green while asserting
/// nothing. "The reader is correct" is this case set, not a one-off mutation run.
/// </summary>
/// <remarks>
/// Every fixture is driven through the REAL reader — <see cref="ConformanceCorpus.LoadGroup(string,
/// string, out List{CorpusCase})"/>, the <c>Wants*</c> accessors,
/// <see cref="ConformanceCorpus.UnassertedExpectations"/> and
/// <see cref="ConformanceCorpus.UnindexedCaseFiles"/> — never a stand-in re-implemented here. A
/// check that cannot fail reads as assurance it does not provide, and a self-test that re-derives
/// the answer it is meant to verify is exactly that check. The group load collects rot rather than
/// stopping at the first break, so one load proves all three structural fixtures and still hands
/// back the cases that DID load for the per-case fixtures below.
/// </remarks>
internal static class ConformanceCorpusSelfTests
{
    internal static int Run()
    {
        TestRun run = new();

        if (ConformanceCorpus.FindCorpusDirectory() is not { } corpusRoot)
        {
            run.Case("self-test corpus reachable", () => run.Fail(
                "could not locate the corpus root, so the self-test fixtures are unreachable — a "
                + "reader nothing exercises proves nothing (HPS-46)"));
            return run.Report("conformance reader self-test");
        }

        string selfTestRoot = Path.Combine(corpusRoot, "self-test");

        // The structural fixtures — missing file, undeclared malformed bytes, duplicate id — are
        // all proven off ONE load, which must fail AND name every break: a reader that stopped at
        // the first would leave the other fixture classes unproven.
        string? rot = ConformanceCorpus.LoadGroup("manifest", selfTestRoot, out List<CorpusCase> loaded);

        run.Case("selfTest.missingFile: a case whose file is gone fails the group load", () =>
        {
            run.True(rot is not null, "the self-test group cannot load cleanly");
            run.Contains(rot, "selfTest.missingFile", "the failure names the fixture");
            run.Contains(rot, "missing vector file", "the failure says the file is gone");
            run.Contains(rot, "does-not-exist.json", "the failure names the missing file");
        });

        run.Case("selfTest.malformedCase: undeclared unparseable case bytes are rot, not data", () =>
        {
            run.True(rot is not null, "the self-test group cannot load cleanly");
            run.Contains(rot, "selfTest.malformedCase", "the failure names the fixture");
            run.Contains(
                rot,
                "malformedJson",
                "the failure says the malformation was never declared — a declared one is data");
            run.True(
                Find(loaded, "selfTest.malformedCase") is null,
                "and the rot did not come back as a usable case");
        });

        run.Case("selfTest.duplicateId: one id on two cases is detected", () =>
        {
            run.True(rot is not null, "the self-test group cannot load cleanly");
            run.Contains(rot, "selfTest.duplicateId", "the failure names the duplicated id");
            run.Contains(rot, "more than once", "the failure says the id repeats");
            run.Equal(
                loaded.FindAll(c => string.Equals(c.Id, "selfTest.duplicateId", StringComparison.Ordinal)).Count,
                1,
                "only the first of the pair loaded — which is why the second is invisible to "
                    + "id-dispatched suites");
        });

        // The per-case fixtures, driven off the cases that DID load out of the rotten index. They
        // prove the asserted-keys mechanics an allow-list reader gets wrong.
        run.Case("selfTest.unknownExpectationKey: a reserved key no host consumes is reported", () =>
        {
            if (Find(loaded, "selfTest.unknownExpectationKey") is not { } corpusCase)
            {
                run.Fail("fixture selfTest.unknownExpectationKey did not load out of the rotten index");
                return;
            }

            run.True(
                ConformanceCorpus.WantsString(corpusCase, "orderId", out _),
                "the case's orderId itself is readable — only the reserved key is broken");

            IReadOnlyList<string> problems = ConformanceCorpus.UnassertedExpectations(
                corpusCase, ManifestConformanceTests.ConsumedExpectationKeys);
            run.Equal(problems.Count, 1, "exactly the reserved key is flagged");
            run.Contains(First(problems), "selfTestNeverConsumed", "the reserved key is named");
            run.Contains(
                First(problems),
                "does not assert",
                "flagged as unknown — no consumed key may ever squat on the selfTest prefix");
        });

        run.Case("selfTest.wrongTypeExpectation: orderId as a number asserts nothing", () =>
        {
            if (Find(loaded, "selfTest.wrongTypeExpectation") is not { } corpusCase)
            {
                run.Fail("fixture selfTest.wrongTypeExpectation did not load out of the rotten index");
                return;
            }

            // The same accessor the real suite runs for orderId: it must read nothing from a
            // number, and that nothing must then be reported rather than counted as covered.
            run.False(
                ConformanceCorpus.WantsString(corpusCase, "orderId", out _),
                "WantsString reads nothing from a number");

            IReadOnlyList<string> problems = ConformanceCorpus.UnassertedExpectations(
                corpusCase, ManifestConformanceTests.ConsumedExpectationKeys);
            run.Equal(problems.Count, 1, "exactly the mistyped key is flagged");
            run.Contains(First(problems), "orderId", "the mistyped key is named");
            run.Contains(
                First(problems),
                "unexpected JSON type",
                "flagged as mistyped, not unknown — an allow-list reader stays green here");
        });

        run.Case("selfTest.nestedUnreadExpectation: the obligation reaches below the top level", () =>
        {
            if (Find(loaded, "selfTest.nestedUnreadExpectation") is not { } corpusCase)
            {
                run.Fail("fixture selfTest.nestedUnreadExpectation did not load out of the rotten index");
                return;
            }

            // Drive it exactly as a top-level-only tracker would: assert both top-level keys, walk
            // the row, read what the host knows how to read.
            run.True(ConformanceCorpus.WantsString(corpusCase, "orderId", out _), "the top-level orderId reads");
            run.True(
                ConformanceCorpus.WantsRows(corpusCase, "items", out IReadOnlyList<ExpectationNode> rows),
                "the top-level items key reads");
            run.Equal(rows.Count, 1, "the fixture has one row");

            ExpectationNode row = rows[0];
            run.Equal(row.Str("orderId"), "ord-selftest-nested", "items[0].orderId reads");

            // An explicit null is a VALUE and counts as read — otherwise `sha256: null`, the row
            // ⛔HPS-27 exists for, becomes the one leaf a suite skips for free.
            run.True(row.Str("sha256") is null, "items[0].sha256 is null, so there is no value to assert");

            // The coercion half (one level down): `status` is a number. A strictly typed read
            // gets NOTHING from it. UE's TryGetStringField would hand back "404" and mark it read.
            run.True(row.Str("status") is null, "a strictly typed read gets nothing from a number");

            // The crux: the top level is entirely satisfied, so a reader that stops there is green.
            run.Equal(
                ConformanceCorpus.UnassertedExpectations(
                    corpusCase, ManifestConformanceTests.ConsumedExpectationKeys).Count,
                0,
                "HPS-46 alone reports this fixture covered — which is why HPS-46b exists");

            IReadOnlyList<string> problems = ConformanceCorpus.UnassertedNestedExpectations(corpusCase);
            run.Equal(problems.Count, 2, $"both unread nested keys are flagged ({string.Join("; ", problems)})");
            run.True(
                problems.Any(p => p.Contains("items[0].selfTestNeverReadNested", StringComparison.Ordinal)),
                "the key nothing asserts is named, with its path");
            run.True(
                problems.Any(p => p.Contains("items[0].status", StringComparison.Ordinal)),
                "and the wrong-typed key fails identically, rather than passing by coercion");
            run.False(
                problems.Any(p => p.Contains("selfTestNote", StringComparison.Ordinal)),
                "prose is exempt at depth, not just at the top level");
            run.False(
                problems.Any(p => p.Contains("items[0].sha256", StringComparison.Ordinal)),
                "and an explicit null that WAS read is not reported");
        });

        run.Case("cases/orphan.json: a file the index never names is found by the sweep", () =>
        {
            string? sweepError = ConformanceCorpus.UnindexedCaseFiles(selfTestRoot, out List<string> swept);
            run.True(sweepError is null, $"the directory sweep runs ({sweepError})");
            run.True(swept.Contains("cases/orphan.json"), "the sweep finds the declared orphan");
            run.Equal(
                swept.Count,
                1,
                "and nothing else — broken-index-*/ carry their own index.json and are nested "
                    + "corpora, not orphans");

            // Cross-check against the index's own declaration: the self-test corpus must be
            // broken in exactly its declared ways, no more and no fewer.
            List<string> declared = DeclaredOrphanFiles(selfTestRoot);
            swept.Sort(StringComparer.Ordinal);
            declared.Sort(StringComparer.Ordinal);
            run.Equal(string.Join(", ", swept), string.Join(", ", declared), "sweep matches `orphanFiles`");
        });

        // The broken-index siblings: each must FAIL to load, never resolve to zero cases.
        run.Case("broken-index-json: an unparseable index fails, never resolves to zero cases", () =>
        {
            string? error = ConformanceCorpus.LoadGroup(
                "manifest", Path.Combine(selfTestRoot, "broken-index-json"), out List<CorpusCase> cases);
            run.True(error is not null, "the load reports failure");
            run.Contains(error, "not valid JSON", "the failure names the parse problem");
            run.Equal(cases.Count, 0, "nothing came back as data");
        });

        run.Case("broken-index-schema: an index with no `cases` fails, never reads as empty", () =>
        {
            string? error = ConformanceCorpus.LoadGroup(
                "manifest", Path.Combine(selfTestRoot, "broken-index-schema"), out List<CorpusCase> cases);
            run.True(error is not null, "the load reports failure");
            run.Contains(error, "cases", "the failure names the missing key");
            run.Equal(cases.Count, 0, "nothing came back as data");
        });

        return run.Report("conformance reader self-test");
    }

    private static CorpusCase? Find(List<CorpusCase> cases, string id)
        => cases.Find(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The self-test index's own <c>orphanFiles</c> declaration — what the sweep is cross-checked
    /// against. Read directly because it is the fixture's statement of intent, not reader output.
    /// </summary>
    private static List<string> DeclaredOrphanFiles(string selfTestRoot)
    {
        List<string> declared = [];

        JsonDocument index;
        try
        {
            index = JsonDocument.Parse(File.ReadAllText(Path.Combine(selfTestRoot, "index.json")));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return declared;
        }

        using (index)
        {
            if (index.RootElement.TryGetProperty("orphanFiles", out JsonElement orphanFiles)
                && orphanFiles.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement orphan in orphanFiles.EnumerateArray())
                {
                    if (orphan.ValueKind == JsonValueKind.String)
                    {
                        declared.Add(orphan.GetString() ?? string.Empty);
                    }
                }
            }
        }

        return declared;
    }

    private static string? First(IReadOnlyList<string> problems)
        => problems.Count > 0 ? problems[0] : null;
}
