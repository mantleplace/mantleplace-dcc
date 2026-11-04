using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

/// <summary>
/// Drives the corpus' <c>manifest</c> group for the <c>autocad</c> host (HPS-40, HPS-41): its own
/// <c>hosts.autocad</c> cases through the import plan, and the host-invariant cases through the shared
/// reader the plugin compiles in.
/// </summary>
/// <remarks>
/// <para>
/// Each AutoCAD case is planned twice, as plain AutoCAD and as Civil 3D, into a drawing with no
/// geographic location, and every expectation is asserted against both plans: the cases say a plain
/// AutoCAD reader and a Civil 3D reader read the same block and agree on everything it states. The
/// values asserted are the PLAN's (what the import would place, where, and in which unit), not a
/// separate parse, so the corpus binds what the drawing receives.
/// </para>
/// <para>
/// Keys are tracked as asserted (HPS-46): a key the corpus adds that this suite does not read fails it.
/// </para>
/// </remarks>
internal static class AutoCadManifestConformanceTests
{
    internal const string HostKey = HostIdentity.Id;

    internal static readonly HashSet<string> ConsumedExpectationKeys =
    [
        "jobId",
        "orderId",
        "autocadFileFrameType",
        "autocadFileFrameCrs",
        "autocadFileFrameBaseCrs",
        "autocadFileFrameOriginLinearUnit",
        "autocadFileFrameHorizontalUnit",
        "autocadFileFrameVerticalUnit",
        "autocadContoursPresent",
        "autocadContoursAbsentReason",
        "autocadContoursPath",
        "autocadContoursHorizontalFrame",
        "autocadContoursHorizontalUnits",
        "autocadContoursVerticalUnits",
        "autocadSurfacePresent",
        "autocadSurfaceAbsentReason",
        "autocadSurfacePath",
        "autocadSurfaceFormat",
        "autocadSurfaceRequires",
        "autocadSurfaceHorizontalFrame",
        "autocadAerialAbsentReason",
        "autocadAerialPath",
        "autocadAerialInsertionX",
        "autocadAerialInsertionY",
        "autocadAerialInsertionWidth",
        "autocadAerialInsertionHeight",
        "autocadAerialInsertionRotationDeg",
        "autocadReadinessContoursPresent",
        "autocadReadinessSurfacePresent",
        "autocadReadinessAerialPresent",
        "autocadRecordsPresent",
        "autocadRecordsAbsentReason",
        "autocadRecordsVerdictStated",
        "autocadRecordsLayers",
        "autocadReadinessRecordsPresent",
        "autocadLineworkPresent",
        "autocadLineworkAbsentReason",
        "autocadLineworkVerdictStated",
        "autocadLineworkPath",
        "autocadLineworkHorizontalFrame",
        "autocadLineworkHorizontalUnits",
        "autocadLineworkVerticalUnits",
        "autocadReadinessLineworkPresent",
    ];

    internal static int Run()
    {
        TestRun run = new();

        if (ConformanceCorpus.LoadGroupFor("manifest", HostKey, out List<CorpusCase> cases) is { } loadError)
        {
            run.Case("corpus loads", () => run.Fail($"conformance corpus unusable: {loadError}"));
            return run.Report("AutoCAD manifest conformance");
        }

        run.Case("the group holds this host's own cases, not only the shared ones", () =>
            run.True(
                cases.Exists(c => string.Equals(c.AppliesTo, HostKey, StringComparison.Ordinal)),
                "at least one case is scoped to autocad"));

        run.Case("no case scoped to another host is driven here", () =>
            run.True(
                cases.TrueForAll(c => c.AppliesTo.Length == 0 || string.Equals(c.AppliesTo, HostKey, StringComparison.Ordinal)),
                "every case is host-invariant or this host's"));

        foreach (CorpusCase corpusCase in cases)
        {
            if (string.Equals(corpusCase.AppliesTo, HostKey, StringComparison.Ordinal))
            {
                run.Case(corpusCase.Id, () => DriveOwnCase(run, corpusCase));
            }
            else
            {
                // The host-invariant cases bind the shared reader this plugin compiles in: the version
                // gate, the order identity and the materialisation verdict it refuses before planning.
                run.Case(corpusCase.Id, () => ManifestConformanceTests.DriveCase(run, corpusCase));
            }
        }

        return run.Report("AutoCAD manifest conformance");
    }

    private static void DriveOwnCase(TestRun run, CorpusCase corpusCase)
    {
        if (!corpusCase.IsAccept)
        {
            run.Fail($"unsupported expect '{corpusCase.Expect}' for an AutoCAD case: teach this suite to drive it");
            return;
        }

        AutoCadImportPlan plain = AutoCadImportPlanner.Plan(corpusCase.Payload, DrawingGeolocation.None, civil3d: null);
        AutoCadImportPlan civil = AutoCadImportPlanner.Plan(corpusCase.Payload, DrawingGeolocation.None, civil3d: DrawingZone.None);

        run.False(plain.IsRefused, $"planned in plain AutoCAD (refused with: {plain.Refusal})");
        run.False(civil.IsRefused, $"planned in Civil 3D (refused with: {civil.Refusal})");

        AssertExpectations(run, corpusCase, plain, "plain AutoCAD");
        AssertExpectations(run, corpusCase, civil, "Civil 3D");

        // What every plan of an AutoCAD case holds, whatever the case asserts.
        run.False(plain.Part(AutoCadPart.Surface).Place, "plain AutoCAD never places the surface: it needs Civil 3D");
        run.Equal(civil.Part(AutoCadPart.Surface).Place, civil.Part(AutoCadPart.Surface).Present, "Civil 3D builds the surface exactly when the bundle ships it");

        foreach (AutoCadImportPlan plan in new[] { plain, civil })
        {
            run.Equal(plan.HorizontalUnit, plan.Frame.HorizontalUnit, "the drawing's X/Y unit is the block's file frame unit");
            run.Equal(plan.VerticalUnit, plan.Frame.VerticalUnit, "the heights' unit is the block's file frame unit");
        }

        foreach (string problem in ConformanceCorpus.UnassertedExpectations(corpusCase, ConsumedExpectationKeys))
        {
            run.Fail($"{problem} (HPS-46).");
        }

        foreach (string problem in ConformanceCorpus.UnassertedNestedExpectations(corpusCase))
        {
            run.Fail(problem);
        }
    }

    private static void AssertExpectations(TestRun run, CorpusCase corpusCase, AutoCadImportPlan plan, string host)
    {
        if (ConformanceCorpus.WantsString(corpusCase, "jobId", out string jobId))
        {
            run.Equal(plan.JobId, jobId, $"{host}: jobId");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "orderId", out string orderId))
        {
            run.Equal(plan.OrderId, orderId, $"{host}: orderId");
        }

        AssertFrame(run, corpusCase, plan.Frame, host);

        PlannedPart contours = plan.Part(AutoCadPart.Contours);
        PlannedPart surface = plan.Part(AutoCadPart.Surface);
        PlannedPart aerial = plan.Part(AutoCadPart.Aerial);

        if (ConformanceCorpus.WantsBool(corpusCase, "autocadContoursPresent", out bool contoursPresent))
        {
            run.Equal(contours.Present, contoursPresent, $"{host}: the contours are present");
            run.Equal(contours.Place, contoursPresent, $"{host}: the contours are placed exactly when present");
        }

        AssertAbsentReason(run, corpusCase, "autocadContoursAbsentReason", contours, host);

        if (ConformanceCorpus.WantsString(corpusCase, "autocadContoursPath", out string contoursPath))
        {
            run.Equal(contours.Path, contoursPath, $"{host}: the contours' path");
            run.True(contours.Place, $"{host}: a contours pointer in the frame is placed ({contours.Sentence})");
            run.Equal(contours.Layer, AutoCadLayers.Contours, $"{host}: the contours' layer");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadContoursHorizontalFrame", out string contoursFrame))
        {
            run.Equal(contours.HorizontalFrame, contoursFrame, $"{host}: the contours' horizontal frame");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadContoursHorizontalUnits", out string contoursX))
        {
            run.Equal(contours.HorizontalUnits, contoursX, $"{host}: the contours' X/Y unit, from the pointer");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadContoursVerticalUnits", out string contoursZ))
        {
            run.Equal(contours.VerticalUnits, contoursZ, $"{host}: the contours' height unit, from the pointer");
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "autocadSurfacePresent", out bool surfacePresent))
        {
            run.Equal(surface.Present, surfacePresent, $"{host}: the surface is present");
        }

        AssertAbsentReason(run, corpusCase, "autocadSurfaceAbsentReason", surface, host);

        if (ConformanceCorpus.WantsString(corpusCase, "autocadSurfacePath", out string surfacePath))
        {
            run.Equal(surface.Path, surfacePath, $"{host}: the surface's path");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadSurfaceFormat", out string surfaceFormat))
        {
            run.Equal(surface.Format, surfaceFormat, $"{host}: the surface's own format word");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadSurfaceRequires", out string requires))
        {
            run.Equal(surface.Requires, requires, $"{host}: the host part the surface's verdict names");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadSurfaceHorizontalFrame", out string surfaceFrame))
        {
            run.Equal(surface.HorizontalFrame, surfaceFrame, $"{host}: the surface's horizontal frame");
        }

        AssertAbsentReason(run, corpusCase, "autocadAerialAbsentReason", aerial, host);

        if (ConformanceCorpus.WantsString(corpusCase, "autocadAerialPath", out string aerialPath))
        {
            run.Equal(aerial.Path, aerialPath, $"{host}: the aerial's path");
            run.True(aerial.Place, $"{host}: an aerial with its insertion is placed ({aerial.Sentence})");
            run.Equal(aerial.Layer, AutoCadLayers.Aerial, $"{host}: the aerial's layer");
        }

        AssertInsertion(run, corpusCase, aerial.Insertion, host);

        if (ConformanceCorpus.WantsBool(corpusCase, "autocadReadinessContoursPresent", out bool contoursReady))
        {
            run.Equal(contours.Ready, contoursReady, $"{host}: the contours' verdict");
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "autocadReadinessSurfacePresent", out bool surfaceReady))
        {
            run.Equal(surface.Ready, surfaceReady, $"{host}: the surface's verdict");
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "autocadReadinessAerialPresent", out bool aerialReady))
        {
            run.Equal(aerial.Ready, aerialReady, $"{host}: the aerial's verdict");
        }

        AssertRecords(run, corpusCase, plan.Part(AutoCadPart.Records), host);
        AssertLinework(run, corpusCase, plan.Part(AutoCadPart.Linework), host);
    }

    private static void AssertRecords(TestRun run, CorpusCase corpusCase, PlannedPart records, string host)
    {
        if (ConformanceCorpus.WantsBool(corpusCase, "autocadRecordsPresent", out bool present))
        {
            run.Equal(records.Present, present, $"{host}: the public-record layers are present");
            run.Equal(records.Place, present, $"{host}: the public-record layers are placed exactly when present ({records.Sentence})");
        }

        AssertAbsentReason(run, corpusCase, "autocadRecordsAbsentReason", records, host);
        AssertVerdictStated(run, corpusCase, "autocadRecordsVerdictStated", records, host);

        if (ConformanceCorpus.WantsRows(corpusCase, "autocadRecordsLayers", out IReadOnlyList<ExpectationNode> rows))
        {
            run.Equal(records.Files.Count, rows.Count, $"{host}: the record layers the block lists, and no others");
            for (int i = 0; i < rows.Count; i++)
            {
                ExpectationNode row = rows[i];
                PlannedFile? file = i < records.Files.Count ? records.Files[i] : null;
                string where = $"{host}: autocadRecordsLayers[{i}]";
                run.Equal(file?.Name, row.Str("name"), $"{where}.name");
                run.Equal(file?.Path, row.Str("path"), $"{where}.path");
                run.Equal(file?.HorizontalFrame, row.Str("horizontalFrame"), $"{where}.horizontalFrame");
                run.Equal(file?.HorizontalUnits, row.Str("horizontalUnits"), $"{where}.horizontalUnits, from the pointer");
                run.Equal(file?.VerticalUnits, row.Str("verticalUnits"), $"{where}.verticalUnits, from the pointer");
                run.Equal(file?.Layer, AutoCadLayers.Record(row.Str("name") ?? string.Empty), $"{where}: on its own layer");
            }
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "autocadReadinessRecordsPresent", out bool ready))
        {
            run.Equal(records.Ready, ready, $"{host}: the public-record layers' verdict");
        }
    }

    private static void AssertLinework(TestRun run, CorpusCase corpusCase, PlannedPart linework, string host)
    {
        if (ConformanceCorpus.WantsBool(corpusCase, "autocadLineworkPresent", out bool present))
        {
            run.Equal(linework.Present, present, $"{host}: the site linework is present");
            run.Equal(linework.Place, present, $"{host}: the site linework is placed exactly when present ({linework.Sentence})");
        }

        AssertAbsentReason(run, corpusCase, "autocadLineworkAbsentReason", linework, host);
        AssertVerdictStated(run, corpusCase, "autocadLineworkVerdictStated", linework, host);

        if (ConformanceCorpus.WantsString(corpusCase, "autocadLineworkPath", out string path))
        {
            run.Equal(linework.Path, path, $"{host}: the linework's path");
            run.Equal(linework.Layer, AutoCadLayers.Linework, $"{host}: the linework's layer");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadLineworkHorizontalFrame", out string frame))
        {
            run.Equal(linework.HorizontalFrame, frame, $"{host}: the linework's horizontal frame");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadLineworkHorizontalUnits", out string x))
        {
            run.Equal(linework.HorizontalUnits, x, $"{host}: the linework's X/Y unit, from the pointer");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadLineworkVerticalUnits", out string z))
        {
            run.Equal(linework.VerticalUnits, z, $"{host}: the linework's height unit, from the pointer");
        }

        if (ConformanceCorpus.WantsBool(corpusCase, "autocadReadinessLineworkPresent", out bool ready))
        {
            run.Equal(linework.Ready, ready, $"{host}: the linework's verdict");
        }
    }

    private static void AssertVerdictStated(TestRun run, CorpusCase corpusCase, string key, PlannedPart part, string host)
    {
        if (!ConformanceCorpus.WantsBool(corpusCase, key, out bool stated))
        {
            return;
        }

        run.Equal(part.VerdictStated, stated, $"{host}: {key}");
        if (!stated)
        {
            run.False(part.Place, $"{host}: a part with no verdict is skipped");
            run.Contains(part.Sentence, "states no verdict", $"{host}: and says the bundle gave none");
        }
    }

    private static void AssertFrame(TestRun run, CorpusCase corpusCase, AutoCadFileFrame frame, string host)
    {
        if (ConformanceCorpus.WantsString(corpusCase, "autocadFileFrameType", out string type))
        {
            run.Equal(frame.Type, type, $"{host}: the file frame's type");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadFileFrameCrs", out string crs))
        {
            run.Equal(frame.Crs, crs, $"{host}: the file frame's CRS");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadFileFrameBaseCrs", out string baseCrs))
        {
            run.Equal(frame.BaseCrs, baseCrs, $"{host}: the local frame's base CRS");
            run.Equal(frame.Crs, string.Empty, $"{host}: a local frame names no CRS of its own");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadFileFrameOriginLinearUnit", out string originUnit))
        {
            run.Equal(frame.OriginLinearUnit, originUnit, $"{host}: the origin keeps its own unit");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadFileFrameHorizontalUnit", out string horizontal))
        {
            run.Equal(frame.HorizontalUnit, horizontal, $"{host}: the file frame's X/Y unit");
        }

        if (ConformanceCorpus.WantsString(corpusCase, "autocadFileFrameVerticalUnit", out string vertical))
        {
            run.Equal(frame.VerticalUnit, vertical, $"{host}: the file frame's height unit");
        }
    }

    private static void AssertAbsentReason(TestRun run, CorpusCase corpusCase, string key, PlannedPart part, string host)
    {
        if (!ConformanceCorpus.WantsString(corpusCase, key, out string reason))
        {
            return;
        }

        run.Equal(part.Reason, reason, $"{host}: {key} echoed as the bundle states it");
        run.False(part.Place, $"{host}: an absent part is skipped");
        run.Contains(part.Sentence, ReadinessReasons.ClauseFor(reason) ?? reason, $"{host}: {key} shown to the user");
    }

    private static void AssertInsertion(TestRun run, CorpusCase corpusCase, AerialInsertion? insertion, string host)
    {
        (string Key, Func<AerialInsertion, double> Read)[] members =
        [
            ("autocadAerialInsertionX", i => i.X),
            ("autocadAerialInsertionY", i => i.Y),
            ("autocadAerialInsertionWidth", i => i.Width),
            ("autocadAerialInsertionHeight", i => i.Height),
            ("autocadAerialInsertionRotationDeg", i => i.RotationDeg),
        ];

        foreach ((string key, Func<AerialInsertion, double> read) in members)
        {
            if (!ConformanceCorpus.WantsDouble(corpusCase, key, out double expected))
            {
                continue;
            }

            run.True(insertion is not null, $"{host}: {key}: the plan carries the aerial's insertion");
            if (insertion is not null)
            {
                // Verbatim: exactly the published number, no tolerance to hide a derivation behind.
                run.True(read(insertion).Equals(expected), $"{host}: {key} is {expected}, as published (got {read(insertion)})");
            }
        }
    }
}
