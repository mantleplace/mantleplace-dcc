namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// <see cref="VectorDocument"/> proving itself, the way <see cref="ConformanceCorpusSelfTests"/>
/// proves the corpus reader.
/// </summary>
/// <remarks>
/// A coverage tracker that silently reports nothing is worse than no tracker: it converts "we
/// checked" into "we believe we checked". Each behaviour below is asserted against a synthetic
/// document rather than against the real corpus, so a corpus edit cannot make these pass by
/// accident.
/// </remarks>
internal static class VectorDocumentSelfTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("a value nothing read is reported, with its path", () =>
        {
            using VectorDocument document = VectorDocument.Parse("""
                { "read": "yes", "ignored": "no" }
                """);

            run.Equal(document.Root.Str("read"), "yes", "read one");
            IReadOnlyList<string> unread = document.UnreadPaths();
            run.Equal(unread.Count, 1, "one value left");
            run.Equal(unread[0], "ignored", "named by path");
        });

        run.Case("array rows are tracked individually — the whole point", () =>
        {
            using VectorDocument document = VectorDocument.Parse("""
                { "rows": [ { "a": 1 }, { "a": 2 }, { "a": 3 } ] }
                """);

            // Driving the first row and stopping is the bug: eleven-row truth tables get one row
            // driven and the case still counts as covered.
            run.Equal(document.Root.Items("rows")[0].Int("a") ?? -1, 1, "drove row 0");

            IReadOnlyList<string> unread = document.UnreadPaths();
            run.Equal(unread.Count, 2, "two rows undriven");
            run.Equal(unread[0], "rows[1].a", "row 1");
            run.Equal(unread[1], "rows[2].a", "row 2");
        });

        run.Case("a wrong-typed read counts as no read at all", () =>
        {
            // Same reasoning as HPS-46's asserted-keys rule: an accessor that returns null for
            // "declared with the wrong type" must not let the value pass as covered.
            using VectorDocument document = VectorDocument.Parse("""{ "count": "3" }""");
            run.True(document.Root.Int("count") is null, "Int declines a string");
            run.Equal(document.UnreadPaths().Count, 1, "and it is still outstanding");
        });

        run.Case("prose is exempt, by key name and by Note suffix", () =>
        {
            using VectorDocument document = VectorDocument.Parse("""
                {
                  "$comment": "why this file exists",
                  "note": "an aside",
                  "rule": "the normative sentence",
                  "vectorFieldsNote": "a longer aside",
                  "rows": [ { "note": "per-row aside", "value": 1 } ]
                }
                """);

            run.Equal(document.Root.Items("rows")[0].Int("value") ?? -1, 1, "drove the data");
            run.Equal(document.UnreadPaths().Count, 0, "and none of the prose is outstanding");
        });

        run.Case("a subtree handed to the parser verbatim counts as consumed", () =>
        {
            // A token-response body is passed to the parser whole. Its fields were consumed, just
            // not one at a time by the test.
            using VectorDocument document = VectorDocument.Parse("""
                { "body": { "access_token": "a", "user": { "id": "u" } }, "parsed": true }
                """);

            run.Equal(document.Root.Bool("parsed") ?? false, true, "drove the expectation");
            run.Equal(document.UnreadPaths().Count, 2, "the body's leaves are outstanding until marked");

            document.Root.MarkConsumed("body");
            run.Equal(document.UnreadPaths().Count, 0, "and marking the subtree clears all of them");
        });

        run.Case("null is a value, and reading it counts", () =>
        {
            // A truth-table row whose expected sha256 is null is the row the ⛔HPS-27 cache rule
            // exists for — unknown is not absent. Treating JSON null as "nothing to track" would
            // make it the one row a suite could skip for free.
            using VectorDocument untouched = VectorDocument.Parse("""{ "sha256": null }""");
            run.Equal(untouched.UnreadPaths().Count, 1, "tracked");
            run.Equal(untouched.UnreadPaths()[0], "sha256", "by path");

            using VectorDocument read = VectorDocument.Parse("""{ "sha256": null }""");
            run.True(read.Root.Str("sha256") is null, "reads as null");
            run.Equal(read.UnreadPaths().Count, 0, "and the read counted");

            using VectorDocument asked = VectorDocument.Parse("""{ "sha256": null, "size": 4 }""");
            run.True(asked.Root.IsNull("sha256"), "explicitly asking is also a read");
            run.False(asked.Root.IsNull("size"), "a present value is not null");
            run.Equal(asked.UnreadPaths().Count, 1, "and asking about a non-null does not consume it");
        });

        return run.Report("vector document self-test");
    }
}
