using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class TerrainSmoothingTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("nothing to say when it was already on", () =>
        {
            run.True(TerrainSmoothing.Notice(wasEnabled: true, isEnabled: true, null) is null,
                "an already-smoothed project produces no sentence");
        });

        run.Case("turning it on names the change, the cost, and the way back", () =>
        {
            string? notice = TerrainSmoothing.Notice(wasEnabled: false, isEnabled: true, null);

            run.Contains(notice, "smooth shading", "what was turned on");
            run.Contains(notice, "project-wide",
                "⛔ the scope — it changes toposolids this plugin never touched");
            run.Contains(notice, "surface patterns",
                "the documented cost: Revit stops drawing them while smoothing is on");
            run.Contains(notice, "paint", "the other documented cost");
            run.Contains(notice, TerrainSmoothing.RibbonPath, "where the curator reverses it");
            run.Contains(notice, "geometry changed",
                "that the terrain itself was not altered — this is display only");
        });

        run.Case("the ribbon path is Revit's own, tab and panel both", () =>
        {
            run.Contains(TerrainSmoothing.RibbonPath, "Massing & Site", "the tab");
            run.Contains(TerrainSmoothing.RibbonPath, "Model Site", "the panel");
            run.Contains(TerrainSmoothing.RibbonPath, "Toposolid Smooth Shading", "the control");
        });

        run.Case("nothing is said about subdivisions, because there is nothing to say", () =>
        {
            string? notice = TerrainSmoothing.Notice(wasEnabled: false, isEnabled: true, null);

            run.True(notice?.Contains("subdivision", StringComparison.Ordinal) == false,
                "no subdivision clause");
            run.Contains(notice, "every toposolid in this project",
                "the scope stated positively instead");
        });

        run.Case("a refusal carries Revit's own words and the manual route", () =>
        {
            string? notice = TerrainSmoothing.Notice(
                wasEnabled: false, isEnabled: false, "The document is read-only.");

            run.Contains(notice, "The document is read-only.",
                "Revit's message verbatim — the half a counter would have discarded");
            run.Contains(notice, TerrainSmoothing.RibbonPath, "how to do it by hand instead");
            run.Contains(notice, "flat triangles", "what the curator will actually see");
            run.True(notice?.Contains("defect", StringComparison.Ordinal) == false,
                "a refusal Revit explained is not reported as a plugin defect");
        });

        run.Case("set and read back off is reported as a defect", () =>
        {
            string? notice = TerrainSmoothing.Notice(wasEnabled: false, isEnabled: false, null);

            run.Contains(notice, "read it back as off", "that the value did not hold");
            run.Contains(notice, "defect", "named as a defect rather than as an ordinary outcome");
            run.Contains(notice, "report it with this log", "what the curator should do about it");
        });

        run.Case("on before and off after is its own defect sentence", () =>
        {
            string? notice = TerrainSmoothing.Notice(wasEnabled: true, isEnabled: false, null);

            run.Contains(notice, "was on before this import", "that something took it away");
            run.Contains(notice, "defect", "named as a defect");
        });

        run.Case("a refusal wins over the read-back, because it explains it", () =>
        {
            string? notice = TerrainSmoothing.Notice(
                wasEnabled: false, isEnabled: false, "Cannot modify a linked document.");

            run.Contains(notice, "Cannot modify a linked document.", "Revit's message");
            run.True(notice?.Contains("defect", StringComparison.Ordinal) == false,
                "not also reported as a plugin defect");
        });

        run.Case("an empty refusal string is not a refusal", () =>
        {
            string? notice = TerrainSmoothing.Notice(wasEnabled: false, isEnabled: true, string.Empty);

            run.Contains(notice, "smooth shading", "the success sentence, not a refusal");
        });

        run.Case("a draped terrain with smoothing ON says the photograph is placed for it", () =>
        {
            string notice = TerrainSmoothing.DrapeNotice(isEnabled: true);

            run.Contains(notice, "placed for Revit's toposolid smooth shading", "which renderer");
            run.Contains(notice, "every triangle", "the symptom of turning it off, as it is seen");
            run.Contains(notice, TerrainSmoothing.RibbonPath, "where the switch is");
            run.Contains(notice, "Turn it back on", "the remedy");
            run.True(notice.Contains("cannot", StringComparison.Ordinal) == false,
                "no longer claims the two cannot be had together");

            run.False(notice.Contains("four quarters", StringComparison.Ordinal),
                "does not predict the symptom of the opposite mismatch");
        });

        run.Case("a draped terrain with smoothing OFF is the loud case, and says to import again", () =>
        {
            string notice = TerrainSmoothing.DrapeNotice(isEnabled: false);

            run.Contains(notice, "could not turn it on", "why the ground is faceted");
            run.Contains(notice, "mosaic", "what the curator will actually see");
            run.Contains(notice, "placed for flat shading", "which renderer the photograph fits");
            run.Contains(notice, "import this bundle again", "the remedy after turning it on by hand");
            run.Contains(notice, TerrainSmoothing.RibbonPath, "where the switch is");
        });

        run.Case("the two drape sentences are different, and only the refused one is a warning", () =>
        {
            string on = TerrainSmoothing.DrapeNotice(isEnabled: true);
            string off = TerrainSmoothing.DrapeNotice(isEnabled: false);

            run.True(on.Length > 0 && off.Length > 0, "both states say something");
            run.True(on != off, "the ordinary case and the refused case do not share a sentence");
            run.True(off.StartsWith("⚠", StringComparison.Ordinal),
                "the case where the photograph is on a mosaic is the warning");
            run.False(on.StartsWith("⚠", StringComparison.Ordinal),
                "the ordinary case is not dressed up as a warning");
        });

        run.Case("smoothing is settled before a polygon step's cuts exactly when the drape will write for them", () =>
        {
            run.True(TerrainSmoothing.SettleBeforeCuts(drapePlanned: true, newSubDivisions: 19), "the drape will write for them");
            run.False(TerrainSmoothing.SettleBeforeCuts(drapePlanned: false, newSubDivisions: 19),
                "no drape: the project-wide setting is left to Finish, as before");
            run.False(TerrainSmoothing.SettleBeforeCuts(drapePlanned: true, newSubDivisions: 0),
                "nothing to cut, nothing to name");
        });

        return run.Report("terrain smooth shading reporting");
    }
}
