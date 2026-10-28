using System.Text;
using MantlePlace.Revit.Client;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The import window opening on what the curator ticked at their last Import: the starting checklist
/// from a remembered choice and the layers a bundle carries, the prerequisites that still govern it,
/// and a remembered file that is not usable.
/// </summary>
/// <remarks>
/// The window only asks the checklist for its starting state, and CI never builds it, so every rule
/// is decided here (<c>HPS-02</c>, <c>HPS-42</c>).
/// </remarks>
internal static class RememberedChoiceTests
{
    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-import-choice-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            StartingChecklistCases(run);
            PrerequisiteCases(run);
            LevelCases(run);
            UnusableFileCases(run, sandbox);
            StoreCases(run, sandbox);
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not a test failure.
            }
        }

        return run.Report("remembered import choice");
    }

    private static void StartingChecklistCases(TestRun run)
    {
        run.Case("no remembered choice starts from the defaults", () =>
        {
            ImportChecklist checklist = new(Enum.GetValues<ImportLayer>());
            checklist.Restore(null);

            foreach (ImportLayer layer in checklist.Layers)
            {
                run.Equal(checklist.IsChecked(layer), ImportLayers.OnByDefault(layer), $"{layer} box");
            }
        });

        run.Case("a bundle that carries the same layers opens as the curator left it", () =>
        {
            ImportChecklist last = new([ImportLayer.Terrain, ImportLayer.ContextBuildings, ImportLayer.Planting, ImportLayer.SiteModel]);
            last.Set(ImportLayer.ContextBuildings, false);
            last.Set(ImportLayer.SiteModel, true);

            ImportChecklist next = new([ImportLayer.Terrain, ImportLayer.ContextBuildings, ImportLayer.Planting, ImportLayer.SiteModel]);
            next.Restore(Pressed(last));

            run.True(next.IsChecked(ImportLayer.Terrain), "terrain stays ticked");
            run.False(next.IsChecked(ImportLayer.ContextBuildings), "a box left unticked stays unticked");
            run.True(next.IsChecked(ImportLayer.Planting), "planting stays ticked");
            run.True(next.IsChecked(ImportLayer.SiteModel), "a box ticked against its default stays ticked");
        });

        run.Case("a bundle that carries more: the extra layers take their defaults", () =>
        {
            ImportChecklist last = new([ImportLayer.Terrain, ImportLayer.Planting]);
            last.Set(ImportLayer.Planting, false);

            ImportChecklist next = new([ImportLayer.Terrain, ImportLayer.Planting, ImportLayer.ContextBuildings, ImportLayer.SiteModel]);
            next.Restore(Pressed(last));

            run.False(next.IsChecked(ImportLayer.Planting), "the remembered layer is as remembered");
            run.True(next.IsChecked(ImportLayer.ContextBuildings), "a layer the last bundle lacked starts at its default, ticked");
            run.False(next.IsChecked(ImportLayer.SiteModel), "and one whose default is unticked stays so");
        });

        run.Case("a bundle that carries fewer: a remembered layer it lacks is not turned on", () =>
        {
            ImportChecklist last = new([ImportLayer.Terrain, ImportLayer.Planting, ImportLayer.ContextBuildings, ImportLayer.SiteModel]);
            last.Set(ImportLayer.SiteModel, true);

            ImportChecklist next = new([ImportLayer.Terrain, ImportLayer.Planting]);
            next.Restore(Pressed(last));

            run.Equal(string.Join(", ", next.Layers), "Terrain, Planting", "the offer is the bundle, not the memory");
            run.False(next.IsChecked(ImportLayer.ContextBuildings), "context buildings are not carried, so not ticked");
            run.False(next.IsChecked(ImportLayer.SiteModel), "nor is the site model");
            run.False(next.Choice.Includes(ImportLayer.SiteModel), "and the choice does not include it");
        });

        run.Case("a bundle that carries different layers: each is remembered or defaulted on its own", () =>
        {
            ImportChecklist last = new([ImportLayer.Terrain, ImportLayer.RoadCentrelines, ImportLayer.LandUsePlan]);
            last.Set(ImportLayer.RoadCentrelines, false);

            ImportChecklist next = new([ImportLayer.RoadCentrelines, ImportLayer.LandUsePlan, ImportLayer.LandCoverPlan, ImportLayer.FloodZones]);
            next.Restore(Pressed(last));

            run.False(next.IsChecked(ImportLayer.RoadCentrelines), "the shared layer left unticked");
            run.True(next.IsChecked(ImportLayer.LandUsePlan), "the shared layer left ticked");
            run.True(next.IsChecked(ImportLayer.LandCoverPlan), "a layer new to the memory takes its default");
            run.True(next.IsChecked(ImportLayer.FloodZones), "and so does the other");
        });

        run.Case("a memory that leaves every box unticked opens with Import dark", () =>
        {
            ImportChecklist last = new([ImportLayer.Terrain, ImportLayer.Planting]);
            last.Set(ImportLayer.Terrain, false);
            last.Set(ImportLayer.Planting, false);

            ImportChecklist next = new([ImportLayer.Terrain, ImportLayer.Planting]);
            next.Restore(Pressed(last));

            run.False(next.CanImport, "nothing ticked is nothing to import, as the window already says");
        });
    }

    private static void PrerequisiteCases(TestRun run)
    {
        run.Case("a remembered tick on a box whose prerequisite is remembered off opens unticked and disabled", () =>
        {
            RememberedChoice remembered = Parsed("""
                { "shape": 1, "layers": { "Terrain": false, "WaterSubdivisions": true, "ImageryDrape": true } }
                """);

            ImportChecklist checklist = new([ImportLayer.Terrain, ImportLayer.WaterSubdivisions, ImportLayer.ImageryDrape]);
            checklist.Restore(remembered);

            run.False(checklist.IsChecked(ImportLayer.Terrain), "terrain is off");
            run.False(checklist.IsEnabled(ImportLayer.WaterSubdivisions), "water needs it, so is disabled");
            run.False(checklist.IsChecked(ImportLayer.WaterSubdivisions), "and shows unticked, whatever the memory says");
            run.True(checklist.MissingPrerequisite(ImportLayer.ImageryDrape) == ImportLayer.Terrain, "with the reason the checklist already gives");
            run.False(checklist.Choice.Includes(ImportLayer.WaterSubdivisions), "so the choice leaves it out");

            checklist.Set(ImportLayer.Terrain, true);
            run.True(checklist.IsChecked(ImportLayer.WaterSubdivisions), "ticking the terrain gives the remembered tick back");
        });

        run.Case("a prerequisite the bundle does not carry does not disable the box, as before", () =>
        {
            RememberedChoice remembered = Parsed("""
                { "shape": 1, "layers": { "Terrain": false, "WaterSubdivisions": true } }
                """);

            ImportChecklist checklist = new([ImportLayer.WaterSubdivisions]);
            checklist.Restore(remembered);

            run.True(checklist.IsEnabled(ImportLayer.WaterSubdivisions), "there is no terrain box to wait on");
            run.True(checklist.IsChecked(ImportLayer.WaterSubdivisions), "so the remembered tick shows");
        });

        run.Case("a pressed choice never records a box the prerequisite kept off as ticked", () =>
        {
            ImportChecklist checklist = new([ImportLayer.Terrain, ImportLayer.WaterSubdivisions]);
            checklist.Set(ImportLayer.Terrain, false);

            RememberedChoice remembered = Pressed(checklist);

            run.True(remembered.Ticked(ImportLayer.WaterSubdivisions) == false, "what was not imported is not remembered as imported");
            run.True(remembered.Ticked(ImportLayer.Terrain) == false, "the terrain was left out");
        });
    }

    private static void LevelCases(TestRun run)
    {
        run.Case("a checklist with no level list ignores a remembered level", () =>
        {
            RememberedChoice remembered = Parsed("""
                { "shape": 1, "layers": { "Planting": true }, "levels": { "Planting": "MED" } }
                """);

            ImportChecklist checklist = new([ImportLayer.Planting]);
            checklist.Restore(remembered);

            run.False(checklist.OffersLevels, "no column");
            run.True(checklist.ChosenLevel(ImportLayer.Planting) == FidelityLevel.Max, "the row stays at MAX");
        });

        run.Case("levels are written only for the rows that list them", () =>
        {
            ImportChecklist checklist = new([ImportLayer.Planting]);
            RememberedChoice remembered = Pressed(checklist);

            run.True(remembered.Level(ImportLayer.Planting) is null, "a row without levels has none to remember");
        });
    }

    private static void UnusableFileCases(TestRun run, string sandbox)
    {
        run.Case("a file that is empty, corrupt, or not an object is no remembered choice", () =>
        {
            run.True(RememberedChoice.Parse(null) is null, "no bytes");
            run.True(RememberedChoice.Parse([]) is null, "an empty file");
            run.True(RememberedChoice.Parse(Encoding.UTF8.GetBytes("{ not json")) is null, "damaged JSON");
            run.True(RememberedChoice.Parse(Encoding.UTF8.GetBytes("[1, 2]")) is null, "an array");
            run.True(RememberedChoice.Parse(Encoding.UTF8.GetBytes("\"Terrain\"")) is null, "a string");
            run.True(RememberedChoice.Parse([0xFF, 0xFE, 0x00]) is null, "bytes that are not text");
        });

        run.Case("a file of an older or newer shape is ignored", () =>
        {
            run.True(
                RememberedChoice.Parse(Encoding.UTF8.GetBytes("""{ "layers": { "Terrain": false } }""")) is null,
                "no shape at all is the older one");
            run.True(
                RememberedChoice.Parse(Encoding.UTF8.GetBytes("""{ "shape": 0, "layers": { "Terrain": false } }""")) is null,
                "an earlier shape");
            run.True(
                RememberedChoice.Parse(Encoding.UTF8.GetBytes("""{ "shape": 2, "layers": { "Terrain": false } }""")) is null,
                "a later shape");
            run.True(
                RememberedChoice.Parse(Encoding.UTF8.GetBytes("""{ "shape": "1", "layers": { "Terrain": false } }""")) is null,
                "a shape that is not a number");
        });

        run.Case("an ignored file starts the checklist from the defaults", () =>
        {
            ImportChecklist checklist = new(Enum.GetValues<ImportLayer>());
            checklist.Restore(RememberedChoice.Parse(Encoding.UTF8.GetBytes("{ not json")));

            foreach (ImportLayer layer in checklist.Layers)
            {
                run.Equal(checklist.IsChecked(layer), ImportLayers.OnByDefault(layer), $"{layer} box");
            }
        });

        run.Case("an entry it does not understand is skipped, and the rest is used", () =>
        {
            RememberedChoice remembered = Parsed("""
                { "shape": 1, "layers": { "Terrain": false, "Hologram": true, "Planting": "yes", "ContextBuildings": false },
                  "levels": { "Planting": "ULTRA", "ImageryDrape": "MED" } }
                """);

            run.True(remembered.Ticked(ImportLayer.Terrain) == false, "a layer it knows");
            run.True(remembered.Ticked(ImportLayer.ContextBuildings) == false, "and another");
            run.True(remembered.Ticked(ImportLayer.Planting) is null, "a tick that is not a boolean is not a choice");
            run.True(remembered.Level(ImportLayer.Planting) is null, "a level that is not one is not a choice");
            run.True(remembered.Level(ImportLayer.ImageryDrape) == FidelityLevel.Med, "a level it knows");
        });

        run.Case("a missing, damaged or older-shaped file on disk reads as nothing", () =>
        {
            run.True(new ImportChoiceStore(Path.Combine(sandbox, "missing.json")).Read() is null, "missing");
            run.False(File.Exists(Path.Combine(sandbox, "missing.json")), "and looking for it does not create it");

            string damaged = Path.Combine(sandbox, "damaged.json");
            File.WriteAllText(damaged, "{ not json");
            run.True(new ImportChoiceStore(damaged).Read() is null, "damaged");

            string older = Path.Combine(sandbox, "older.json");
            File.WriteAllText(older, """{ "layers": { "Terrain": false } }""");
            run.True(new ImportChoiceStore(older).Read() is null, "older shape");

            string locked = Path.Combine(sandbox, "locked.json");
            File.WriteAllText(locked, "{}");
            using FileStream hold = new(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            run.True(new ImportChoiceStore(locked).Read() is null, "held open by another process");
        });
    }

    private static void StoreCases(TestRun run, string sandbox)
    {
        run.Case("what is written is read back by the next process", () =>
        {
            string path = Path.Combine(sandbox, "choice", "import-choice.json");
            ImportChecklist checklist = new([ImportLayer.Terrain, ImportLayer.ContextBuildings, ImportLayer.SiteModel]);
            checklist.Set(ImportLayer.ContextBuildings, false);
            checklist.Set(ImportLayer.SiteModel, true);

            run.True(new ImportChoiceStore(path).Write(Pressed(checklist)), "written, creating its folder");

            ImportChecklist next = new([ImportLayer.Terrain, ImportLayer.ContextBuildings, ImportLayer.SiteModel]);
            next.Restore(new ImportChoiceStore(path).Read());
            run.True(next.IsChecked(ImportLayer.Terrain), "terrain");
            run.False(next.IsChecked(ImportLayer.ContextBuildings), "context buildings, left out");
            run.True(next.IsChecked(ImportLayer.SiteModel), "the site model, ticked against its default");
        });

        run.Case("the latest Import replaces the last, and a write the file refuses is dropped", () =>
        {
            string path = Path.Combine(sandbox, "replace.json");
            ImportChoiceStore store = new(path);
            ImportChecklist first = new([ImportLayer.Terrain]);
            first.Set(ImportLayer.Terrain, false);
            store.Write(Pressed(first));
            store.Write(Pressed(new ImportChecklist([ImportLayer.Terrain])));

            run.True(store.Read()!.Ticked(ImportLayer.Terrain) == true, "the second Import is what is remembered");

            using FileStream hold = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            run.False(store.Write(Pressed(first)), "a locked file is not an error, just not written");
        });
    }

    private static RememberedChoice Pressed(ImportChecklist checklist) => RememberedChoice.Of(checklist, checklist.Choice);

    private static RememberedChoice Parsed(string json)
        => RememberedChoice.Parse(Encoding.UTF8.GetBytes(json)) ?? throw new InvalidOperationException("the fixture is not a remembered choice");
}
