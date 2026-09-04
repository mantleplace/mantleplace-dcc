using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The sentence a curator reads after the import turns on toposolid smooth shading.
/// </summary>
/// <remarks>
/// Two things are held in place here, and neither is cosmetic. The first is that a plugin which
/// flips a <em>project-wide</em> display setting must say so, must say what it costs, and must say
/// where to reverse it — a curator whose toposolid surface patterns stop drawing has to be able to
/// connect that to this import rather than to a corrupt project. The second is that a setting
/// written and read back as off is a defect and has to read as one, which is the lesson the drape's
/// texture distances taught at the price of two sessions.
/// </remarks>
internal static class TerrainSmoothingTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("nothing to say when it was already on", () =>
        {
            // Null, not "". A re-import onto a project that is already smoothed has genuinely
            // nothing to report, and an empty clause would still cost a line in the summary.
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
            // Named from Revit's UI rather than paraphrased: a curator following this sentence is
            // looking at a ribbon, and "somewhere under Massing" does not find a checkbox.
            run.Contains(TerrainSmoothing.RibbonPath, "Massing & Site", "the tab");
            run.Contains(TerrainSmoothing.RibbonPath, "Model Site", "the panel");
            run.Contains(TerrainSmoothing.RibbonPath, "Toposolid Smooth Shading", "the control");
        });

        run.Case("nothing is said about subdivisions, because there is nothing to say", () =>
        {
            // ⛔ The setting is document-wide by SIGNATURE — SetSmoothedSurface is static and takes
            // only a Document, so there is no per-toposolid state to report on. A sentence hinting
            // that subdivisions were handled separately would describe an API that does not exist,
            // and would send the next reader looking for a loop that must not be written.
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
            // ⛔ The exact shape of the drape's texture-distance bug: the call returned without
            // complaint and the document held something else. Only the read-back sees it.
            string? notice = TerrainSmoothing.Notice(wasEnabled: false, isEnabled: false, null);

            run.Contains(notice, "read it back as off", "that the value did not hold");
            run.Contains(notice, "defect", "named as a defect rather than as an ordinary outcome");
            run.Contains(notice, "report it with this log", "what the curator should do about it");
        });

        run.Case("on before and off after is its own defect sentence", () =>
        {
            // A different bug from one that never took the setting: something in this import took
            // it away. Worth its own words, because the two have different causes.
            string? notice = TerrainSmoothing.Notice(wasEnabled: true, isEnabled: false, null);

            run.Contains(notice, "was on before this import", "that something took it away");
            run.Contains(notice, "defect", "named as a defect");
        });

        run.Case("a refusal wins over the read-back, because it explains it", () =>
        {
            // Both are true at once on the refusal path — Revit threw, so of course it reads off.
            // The message is the useful half and the generic defect line would bury it.
            string? notice = TerrainSmoothing.Notice(
                wasEnabled: false, isEnabled: false, "Cannot modify a linked document.");

            run.Contains(notice, "Cannot modify a linked document.", "Revit's message");
            run.True(notice?.Contains("defect", StringComparison.Ordinal) == false,
                "not also reported as a plugin defect");
        });

        run.Case("an empty refusal string is not a refusal", () =>
        {
            // Guards a caller that initialises the message to "" rather than null: an empty
            // fragment must not become a sentence that trails off after "Revit said".
            string? notice = TerrainSmoothing.Notice(wasEnabled: false, isEnabled: true, string.Empty);

            run.Contains(notice, "smooth shading", "the success sentence, not a refusal");
        });

        run.Case("a draped terrain with smoothing ON gets the loud sentence", () =>
        {
            // ⛔ The case that cost a whole session. Smoothing and a real-world-scaled photograph
            // cannot coexist: the imagery renders as four quarters meeting at a cross. A curator who
            // has the setting on has no way whatsoever to connect that to a ribbon toggle, so this
            // sentence has to name the symptom they can see, not just the setting.
            string notice = TerrainSmoothing.DrapeNotice(isEnabled: true);

            run.Contains(notice, "four quarters", "the symptom, in the shape they will recognise");
            run.Contains(notice, TerrainSmoothing.RibbonPath, "the switch that fixes it");
            run.Contains(notice, "has not changed the setting for you",
                "that the plugin declined to trespass on a project-wide setting");
        });

        run.Case("a draped terrain with smoothing OFF explains why the ground is faceted", () =>
        {
            // The ordinary path, and it still owes an explanation: the faceting is the very thing
            // the issue was raised about, so silence here reads as the defect being unaddressed.
            string notice = TerrainSmoothing.DrapeNotice(isEnabled: false);

            run.Contains(notice, "flat triangles", "the appearance being explained");
            run.Contains(notice, "deliberately left it off", "that this was a choice, not an omission");
            run.Contains(notice, "breaks the mapping", "the reason for that choice");
            run.Contains(notice, TerrainSmoothing.RibbonPath, "where to overrule it");
        });

        run.Case("the two drape sentences are different, and neither is empty", () =>
        {
            string on = TerrainSmoothing.DrapeNotice(isEnabled: true);
            string off = TerrainSmoothing.DrapeNotice(isEnabled: false);

            run.True(on.Length > 0 && off.Length > 0, "both states say something");
            run.True(on != off, "the loud case and the ordinary case do not share a sentence");
            run.True(on.StartsWith("⚠", StringComparison.Ordinal),
                "only the case that needs the curator to act is marked as a warning");
            run.False(off.StartsWith("⚠", StringComparison.Ordinal),
                "the ordinary case is not dressed up as a warning");
        });

        return run.Report("terrain smooth shading reporting");
    }
}
