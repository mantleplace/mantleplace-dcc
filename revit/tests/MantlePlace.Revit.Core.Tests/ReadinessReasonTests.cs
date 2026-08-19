using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// <c>dcc_readiness.&lt;host&gt;.&lt;path&gt;.reason</c> is an OPEN vocabulary until manifest v19
/// closes it, and <c>HPS-36</c> requires the manifest's own reason to be surfaced rather than
/// replaced. Surfacing it and printing it verbatim are not the same thing.
/// </summary>
internal static class ReadinessReasonTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("the five closed-set tokens each get their own sentence", () =>
        {
            // NOT_DELIVERED_REASONS, the bounded half of the vocabulary.
            foreach (string token in new[]
                     {
                         "no_features_in_aoi",
                         "emit_failed",
                         "area_cap_exceeded",
                         "available_on_request",
                         "outside_coverage",
                     })
            {
                string? clause = ReadinessReasons.ClauseFor(token);
                run.True(clause is not null, $"{token} is recognised");
                run.False(clause?.Contains(token, StringComparison.Ordinal) ?? true, $"{token} is not echoed raw");
            }
        });

        run.Case("the six *_not_produced literals collapse to one sentence", () =>
        {
            string? points = ReadinessReasons.ClauseFor("points_csv_not_produced");
            string? dxf = ReadinessReasons.ClauseFor("surface_dxf_not_produced");
            string? ifc = ReadinessReasons.ClauseFor("ifc_site_not_produced");
            run.True(points is not null, "points_csv_not_produced is recognised");
            run.Equal(dxf, points, "surface_dxf_not_produced says the same thing");
            run.Equal(ifc, points, "ifc_site_not_produced says the same thing");
        });

        run.Case("a curator choice reads as a choice, not as a failure", () =>
        {
            // "You didn't order this" and "we couldn't make it" are different sentences.
            string? clause = ReadinessReasons.ClauseFor("deselected_by_packaging_selection");
            run.True(clause is not null, "recognised");
            run.False(clause!.Contains("could not", StringComparison.OrdinalIgnoreCase), "not phrased as a failure");
        });

        run.Case("emit_threw:<stage> is matched on its prefix and the stage never reaches prose", () =>
        {
            // The defect this guards: a raw internal stage token shown to a human as if it
            // were an explanation. The prefix is bounded even though the suffix is not.
            string? clause = ReadinessReasons.ClauseFor("emit_threw:mesh_stage_3");
            run.True(clause is not null, "recognised by prefix");
            run.False(clause!.Contains("mesh_stage_3", StringComparison.Ordinal), "the stage token is not in the clause");
            run.False(clause.Contains("emit_threw", StringComparison.Ordinal), "the token itself is not in the clause");
        });

        run.Case("an unrecognised but token-shaped value is framed as a code, never as prose", () =>
        {
            string? clause = ReadinessReasons.ClauseFor("some_future_token_v21");
            run.True(clause is not null, "still produces a clause");
            run.Contains(clause, "\"some_future_token_v21\"", "the token is quoted, and preserved for diagnosis");
        });

        run.Case("a reason that is not token-shaped is reported WITHOUT quoting it", () =>
        {
            // A sidecar that captured an exception message instead of a token must not dump a
            // paragraph into a TaskDialog — but the fact that the bundle gave a reason at all is
            // still worth saying, so the clause survives and only the payload is dropped.
            foreach (string notAToken in new[]
                     {
                         new string('x', 4000),
                         "could not open the source raster: permission denied",
                         "emit\nthrew",
                     })
            {
                string? clause = ReadinessReasons.ClauseFor(notAToken);
                run.True(clause is not null, "still produces a clause");
                run.True(clause!.Length < 200, $"clause is bounded, got {clause.Length} characters");
                run.False(clause.Contains('\n', StringComparison.Ordinal), "no newline reaches the dialog");
                run.False(clause.Contains("permission denied", StringComparison.Ordinal), "no prose payload");
                run.False(clause.Contains("xxxx", StringComparison.Ordinal), "no runaway payload");
            }
        });

        run.Case("absent and blank reasons produce no clause at all", () =>
        {
            run.True(ReadinessReasons.ClauseFor(null) is null, "null");
            run.True(ReadinessReasons.ClauseFor("") is null, "empty");
            run.True(ReadinessReasons.ClauseFor("   ") is null, "whitespace");
        });

        return run.Report("readiness reason presentation");
    }
}
