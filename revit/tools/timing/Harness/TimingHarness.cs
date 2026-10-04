using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Addin;
using MantlePlace.Revit.Core;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace MantlePlace.Scratch.Timing;

// Real-Revit timing harness for the Mantle Place Revit plugin. It does nothing unless the Revit it is loaded into was
// launched by Invoke-TimingRun.ps1 with MANTLEPLACE_TIMING_TAG equal to the tag compiled into this assembly
// (BuildInfo.Tag, see OnStartup), so a user's Revit, or another agent's Revit that happens to read this
// add-in's temporary manifest, never runs it. Declining leaves one line in %TEMP%\mantleplace-timing-declined.log.
//
// Two phases, chosen by MANTLEPLACE_TIMING_PHASE:
//   layers  ApplicationInitialized: a new project (or the run dir's project.rvt when the driver copied a
//           checkpoint there), ActiveImport.Open, Begin(ImportLayerChoice.Only([...])), RunToEnd. No window,
//           no UI Automation. Optionally saves the project after the import as a checkpoint.
//           With MANTLEPLACE_TIMING_STOP_BEFORE=<ImportLayer> the run steps the staged import slice by slice
//           instead of RunToEnd and stops at the boundary in front of that layer's step: every earlier step
//           committed, the named one not started (see StepUntilBefore). The checkpoint is then the project
//           as a full import leaves it at that point.
//   window  the real path a curator gets: a project opened and activated by an ExternalEvent, the import
//           opened with ActiveImport.Open and handed to the handler with TakeOver, which shows the real
//           import window on its checklist. The driver ticks the boxes and presses Import through UI
//           Automation; this side only watches ActiveImport/IsImporting to timestamp the run.
public sealed class Harness : IExternalApplication
{
    private static readonly object Gate = new();
    private static string _dir = "";
    private static string _phase = "";

    // window phase
    private static BundleImportEventHandler? _handler;
    private static ExternalEvent? _openEvent;
    private static volatile ActiveImport? _active;
    private static volatile bool _openFailed;
    private static DateTime _beganAt;
    private static DateTime _endedAt;

    // The tag this assembly was built for: a const in BuildInfo.g.cs, written by Build-TimingHarness.ps1 and read back out of
    // the finished DLL there. Not read from the environment, not from a file beside the DLL.
    private static string Tag => BuildInfo.Tag;

    private static string Meta(string key)
        => typeof(Harness).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "";

    private static string Env(string name) => Environment.GetEnvironmentVariable("MANTLEPLACE_TIMING_" + name) ?? "";

    public Result OnStartup(UIControlledApplication application)
    {
        // The gate. Revit watches Addins\<year> while it runs, so a same-year Revit that is already up can
        // pick up ANOTHER run's temporary manifest and load this DLL into its own process, which carries its own
        // run's MANTLEPLACE_TIMING_* environment. Unless that process was launched for exactly this tag, do nothing
        // at all: no event subscription, no thread, no file in any run dir. The one exception is a single line in
        // %TEMP%\mantleplace-timing-declined.log, so the refusal can be seen.
        string processTag = Env("TAG");
        if (BuildInfo.Tag.Length == 0 || processTag != BuildInfo.Tag || string.IsNullOrEmpty(Env("DIR")))
        {
            Decline(processTag);
            return Result.Succeeded;
        }

        _dir = Env("DIR");
        _phase = Env("PHASE");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "started.txt"), Now());

        switch (_phase)
        {
            case "layers":
                application.ControlledApplication.ApplicationInitialized += (sender, _) => RunLayers((RevitApplication)sender!);
                break;
            case "window":
                _handler = new BundleImportEventHandler();
                ExternalEvent importEvent = ExternalEvent.Create(_handler);
                _handler.Attach(importEvent);
                _openEvent = ExternalEvent.Create(new Handler("open", OpenForWindow));
                application.ControlledApplication.ApplicationInitialized += (_, _) => new Thread(Watch) { IsBackground = true, Name = "timing watcher" }.Start();
                break;
            default:
                Say($"unknown phase '{_phase}'");
                Done("bad phase");
                break;
        }

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    /// <summary>The only thing a declining harness does: one line in %TEMP%\mantleplace-timing-declined.log. Never throws.</summary>
    private static void Decline(string processTag)
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            string dirState = string.IsNullOrEmpty(Env("DIR")) ? "unset" : "set";
            string line = $"{stamp} declined: compiled tag '{BuildInfo.Tag}', this process's MANTLEPLACE_TIMING_TAG '{processTag}', MANTLEPLACE_TIMING_DIR {dirState}, pid {Environment.ProcessId}, assembly {typeof(Harness).Assembly.Location}";
            using FileStream stream = new(Path.Combine(Path.GetTempPath(), "mantleplace-timing-declined.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using StreamWriter writer = new(stream);
            writer.WriteLine(line);
        }
        catch (Exception)
        {
            // A refusal that cannot be logged is still a refusal; Revit must not notice either way.
        }
    }

    // ---- shared ---------------------------------------------------------------------------------

    private static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff");

    private static void Say(string line)
    {
        lock (Gate)
        {
            File.AppendAllText(Path.Combine(_dir, "harness.txt"), $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
    }

    private static void Done(string outcome) => File.WriteAllText(Path.Combine(_dir, "done.txt"), $"{Now()} {outcome}");

    private static Dictionary<string, object?> Facts(RevitApplication app) => new()
    {
        ["tag"] = Tag,
        ["source_commit"] = Meta("SourceCommit"),
        ["source_root"] = Meta("SourceRoot"),
        ["plugin_version"] = PluginVersion.Current,
        ["phase"] = _phase,
        ["pid"] = Environment.ProcessId,
        ["revit_version"] = app.VersionNumber,
        ["revit_name"] = app.VersionName,
        ["revit_build"] = app.VersionBuild,
        ["revit_subversion"] = app.SubVersionNumber,
    };

    private static void WriteResult(Dictionary<string, object?> facts)
        => File.WriteAllText(Path.Combine(_dir, "result.json"), JsonSerializer.Serialize(facts, new JsonSerializerOptions { WriteIndented = true }));

    private static string Bundle => Env("BUNDLE");

    private static string ProjectPath => Path.Combine(_dir, "project.rvt");

    /// <summary>The run dir's project.rvt when the driver put a checkpoint copy there, else a fresh metric project saved there.</summary>
    private static (Document Document, bool FromCheckpoint) OpenProject(RevitApplication app, UIApplication? ui)
    {
        bool fromCheckpoint = File.Exists(ProjectPath);
        if (!fromCheckpoint)
        {
            Document blank = app.NewProjectDocument(Autodesk.Revit.DB.UnitSystem.Metric);
            blank.SaveAs(ProjectPath, new SaveAsOptions { OverwriteExistingFile = true });
            blank.Close(false);
        }

        Stopwatch clock = Stopwatch.StartNew();
        Document document = ui is null ? app.OpenDocumentFile(ProjectPath) : ui.OpenAndActivateDocument(ProjectPath).Document;
        Say($"{(fromCheckpoint ? "opened the checkpoint copy" : "opened a fresh project")} in {clock.Elapsed.TotalSeconds:N1} s");
        return (document, fromCheckpoint);
    }

    // ---- layers phase ---------------------------------------------------------------------------

    private static ImportLayerChoice ParseLayers(string list, out string[] names)
    {
        string[] parts = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("MANTLEPLACE_TIMING_LAYERS is empty. Valid layers: " + string.Join(", ", Enum.GetNames<ImportLayer>()) + ", or All.");
        }

        if (parts.Length == 1 && parts[0].Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            names = [.. Enum.GetNames<ImportLayer>()];
            return Env("LEVELS").Length == 0
                ? ImportLayerChoice.All
                : ImportLayerChoice.Only(Enum.GetValues<ImportLayer>(), ParseLevels(Env("LEVELS"), Enum.GetValues<ImportLayer>()));
        }

        // The checklist's own defaults in the source this tag was built from: the boxes a curator who changes
        // nothing imports, so the layers phase can time them without the window.
        if (parts.Length == 1 && parts[0].Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            ImportLayer[] defaults = [.. Enum.GetValues<ImportLayer>().Where(ImportLayers.OnByDefault)];
            names = [.. defaults.Select(layer => layer.ToString())];
            return ImportLayerChoice.Only(defaults, ParseLevels(Env("LEVELS") is { Length: > 0 } given ? given : "Default", defaults));
        }

        List<ImportLayer> layers = [];
        foreach (string part in parts)
        {
            string squashed = part.Replace(" ", string.Empty);
            if (!Enum.TryParse(squashed, ignoreCase: true, out ImportLayer layer) || !Enum.IsDefined(layer))
            {
                throw new ArgumentException($"'{part}' is not an ImportLayer. Valid: " + string.Join(", ", Enum.GetNames<ImportLayer>()) + ", or All.");
            }

            layers.Add(layer);
        }

        names = [.. layers.Select(layer => layer.ToString())];
        return ImportLayerChoice.Only(layers, ParseLevels(Env("LEVELS"), layers));
    }

    // MANTLEPLACE_TIMING_LEVELS: 'Default' (each layer at ImportLayers.DefaultLevel of the source this tag was
    // built from, what the window opens on), 'All=Min' (every layer at one level), or 'Planting=Min,RoadSubdivisions=Med'.
    // Empty means every layer at MAX, as before levels existed. A level the bundle publishes in a form the step cannot
    // take is refused by the planner, as the window would never offer it.
    private static Dictionary<ImportLayer, FidelityLevel>? ParseLevels(string list, IEnumerable<ImportLayer> layers)
    {
        string[] parts = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        if (parts.Length == 1 && parts[0].Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            return layers.ToDictionary(layer => layer, ImportLayers.DefaultLevel);
        }

        Dictionary<ImportLayer, FidelityLevel> levels = [];
        foreach (string part in parts)
        {
            string[] pair = part.Split('=', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !FidelityLevelNames.TryParse(pair[1].ToUpperInvariant(), out FidelityLevel level))
            {
                throw new ArgumentException($"MANTLEPLACE_TIMING_LEVELS '{part}' is not <ImportLayer>=<RAW|MAX|MED|MIN>, 'All=<level>' or 'Default'.");
            }

            if (pair[0].Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                foreach (ImportLayer layer in layers)
                {
                    levels[layer] = level;
                }

                continue;
            }

            if (!Enum.TryParse(pair[0].Replace(" ", string.Empty), ignoreCase: true, out ImportLayer named) || !Enum.IsDefined(named))
            {
                throw new ArgumentException($"MANTLEPLACE_TIMING_LEVELS '{part}': '{pair[0]}' is not an ImportLayer. Valid: " + string.Join(", ", Enum.GetNames<ImportLayer>()) + ", or All.");
            }

            levels[named] = level;
        }

        return levels;
    }

    private static ImportLayer? ParseStopBefore(string text)
    {
        string squashed = text.Replace(" ", string.Empty);
        if (squashed.Length == 0)
        {
            return null;
        }

        if (!Enum.TryParse(squashed, ignoreCase: true, out ImportLayer layer) || !Enum.IsDefined(layer))
        {
            throw new ArgumentException($"MANTLEPLACE_TIMING_STOP_BEFORE '{text}' is not an ImportLayer. Valid: " + string.Join(", ", Enum.GetNames<ImportLayer>()) + ".");
        }

        return layer;
    }

    /// <summary>The staged import is between two steps, and the next slice would start step <paramref name="target"/>.</summary>
    private static bool AtBoundary(StagedImport staged, int target)
        => staged.Current is null
           && !staged.IsFinished
           && staged.Steps[target].State == ImportStepState.Waiting
           && staged.Steps.Take(target).All(step => step.State is ImportStepState.Done or ImportStepState.Failed);

    /// <summary>
    /// Runs the staged import one slice at a time and stops in front of the first step that builds
    /// <paramref name="layer"/>: every step before it has ended, the named one has not started (its
    /// "[Kind] started." marker is never written), and no transaction is open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Steps with <see cref="ActiveImport.Advance"/>, the slice the real import window raises, so each
    /// slice runs under the importer's failure hook exactly as in a full import.
    /// </para>
    /// <para>
    /// ⛔ It does NOT stop by cancelling through <see cref="ActiveImport.Advance"/>. That call, when the
    /// staged import reports it is over, runs <c>RevitBundleImporter.Finish()</c>, which is
    /// <c>EnsureSmoothedSurface()</c>: a committed transaction (terrain smooth shading, 7.3 s on the
    /// test order) that a full import only performs INSIDE the drape step. A checkpoint saved after it
    /// would have the drape's first commit already paid, and a drape run on it would not measure the
    /// drape. So this stops calling <c>ActiveImport.Advance</c> at the boundary. The one thing it does
    /// afterwards is the staged import's own cancel, <see cref="StagedImport.RequestCancel"/> then
    /// <see cref="StagedImport.Advance"/> called on the <see cref="StagedImport"/> itself: with no step in
    /// flight that is pure bookkeeping (the waiting steps become NotRun and it writes the outcome text),
    /// it calls nothing on the Revit side, and it leaves the import in the state a curator's Cancel
    /// between two steps would.
    /// </para>
    /// </remarks>
    /// <returns><c>ok</c> when the boundary was reached with every earlier step Done; otherwise the outcome to record.</returns>
    private static string StepUntilBefore(ActiveImport import, ImportLayer layer, Dictionary<string, object?> facts)
    {
        StagedImport staged = import.Staged ?? throw new InvalidOperationException("Begin has not staged the import.");
        IReadOnlyList<StagedStep> steps = staged.Steps;
        facts["planned_steps"] = steps.Select(step => step.Step.Kind.ToString()).ToArray();
        facts["stop_before_layer"] = layer.ToString();

        int target = -1;
        for (int i = 0; i < steps.Count && target < 0; i++)
        {
            if (ImportLayers.Of(steps[i].Step.Kind) == layer)
            {
                target = i;
            }
        }

        if (target < 0)
        {
            string message = $"the plan has no step for {layer}, so nothing was run. Planned: " + string.Join(", ", steps.Select(step => step.Step.Kind));
            facts["stop_before_reached"] = false;
            facts["stop_before_message"] = message;
            Say("STOP-BEFORE: " + message);
            WriteStopRecord([$"stop before: {layer}", "reached: NO", message]);
            return "stop-not-reached";
        }

        ImportStepKind named = steps[target].Step.Kind;
        facts["stop_before_step"] = named.ToString();
        facts["stop_before_step_number"] = target + 1;
        facts["stop_before_step_count"] = steps.Count;
        Say($"stepping the staged import slice by slice; it stops before step {target + 1} of {steps.Count}, {named} (layer {layer})");

        int slices = 0;
        bool reached = AtBoundary(staged, target);
        while (!reached && import.Advance())
        {
            slices++;
            reached = AtBoundary(staged, target);
        }

        facts["stop_before_slices"] = slices;
        facts["stop_before_reached"] = reached;
        if (!reached)
        {
            string ended = string.Join(", ", steps.Select(step => $"{step.Step.Kind}={step.State}"));
            facts["stop_before_message"] = "the import ended before it reached the boundary: " + ended;
            Say("STOP-BEFORE: not reached. " + ended);
            WriteStopRecord([$"stop before: {layer} (step {target + 1} of {steps.Count}, {named})", "reached: NO", "the import ended first: " + ended]);
            return import.Failed ? "import-failed" : "stop-not-reached";
        }

        // Nothing is in flight: pure bookkeeping, no Revit call, no Finish().
        staged.RequestCancel();
        staged.Advance();

        string[] ran = [.. steps.Take(target).Select(step => $"{step.Step.Kind}={step.State}")];
        string[] notRun = [.. steps.Skip(target).Select(step => $"{step.Step.Kind}={step.State}")];
        bool clean = steps.Take(target).All(step => step.State == ImportStepState.Done)
                     && steps.Skip(target).All(step => step.State == ImportStepState.NotRun);
        facts["steps_ran"] = ran;
        facts["steps_not_run"] = notRun;
        facts["stop_before_clean"] = clean;
        facts["staged_outcome"] = staged.Outcome;
        facts["heading"] = $"The harness stopped the import before {named} (step {target + 1} of {steps.Count}).";
        facts["summary_head"] = null;

        List<string> record =
        [
            $"stop before: {layer} (step {target + 1} of {steps.Count}, {named})",
            $"reached: yes, after {slices} slices; clean = {clean} (every earlier step Done, the named step and every later one NotRun)",
            $"steps that ran ({ran.Length}): " + string.Join(", ", ran),
            $"steps not run ({notRun.Length}), the named one first: " + string.Join(", ", notRun),
            "the named step's \"[" + named + "] started.\" marker is not written by a step that never started; the driver counts it in import.log afterwards.",
            "the staged import's own outcome text: " + staged.Outcome,
            "stopped by not calling ActiveImport.Advance again (its Finish() would commit terrain smooth shading, which a full import does only inside the drape step); the staged import's cancel was used for bookkeeping only.",
        ];
        WriteStopRecord(record);
        Say($"STOP-BEFORE: at the boundary in front of {named}; ran {ran.Length} steps; clean={clean}");
        return clean ? "ok" : "earlier-step-failed";
    }

    private static void WriteStopRecord(IEnumerable<string> lines)
        => File.WriteAllLines(Path.Combine(_dir, "stop-before.txt"), lines);

    private static void RunLayers(RevitApplication app)
    {
        Dictionary<string, object?> facts = Facts(app);
        Say($"Revit {app.VersionNumber} {app.VersionName} build {app.VersionBuild}, pid {Environment.ProcessId}; plugin {PluginVersion.Current}");
        string outcome = "ok";
        try
        {
            ImportLayerChoice choice = ParseLayers(Env("LAYERS"), out string[] names);
            facts["layers"] = names;
            facts["levels"] = Env("LEVELS");
            facts["bundle"] = Bundle;
            ImportLayer? stopBefore = ParseStopBefore(Env("STOP_BEFORE"));
            if (stopBefore is { } named)
            {
                facts["stop_before_layer"] = named.ToString();
            }

            (Document document, bool fromCheckpoint) = OpenProject(app, ui: null);
            facts["from_checkpoint"] = fromCheckpoint;

            Stopwatch clock = Stopwatch.StartNew();
            ActiveImport? import = ActiveImport.Open(app, document, Bundle, out ImportRefusal? refusal);
            facts["open_seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 2);
            if (import is null)
            {
                outcome = "refused";
                facts["refusal"] = refusal?.Message;
                Say("REFUSED " + refusal?.Message);
            }
            else
            {
                using (import)
                {
                    Say($"import of [{string.Join(", ", names)}] begins; ActiveImport.Open took {clock.Elapsed.TotalSeconds:N1} s");
                    DateTime beganAt = DateTime.Now;
                    Stopwatch run = Stopwatch.StartNew();
                    import.Begin(choice);
                    facts["begin_seconds"] = Math.Round(run.Elapsed.TotalSeconds, 2);
                    string stopOutcome = "ok";
                    if (stopBefore is { } stopLayer)
                    {
                        stopOutcome = StepUntilBefore(import, stopLayer, facts);
                    }
                    else
                    {
                        import.RunToEnd();
                    }

                    run.Stop();
                    facts["began_at"] = beganAt.ToString("yyyy-MM-ddTHH:mm:ss.fff");
                    facts["ended_at"] = Now();
                    facts["wall_seconds"] = Math.Round(run.Elapsed.TotalSeconds, 2);
                    facts["failed"] = import.Failed;
                    if (stopBefore is null)
                    {
                        facts["heading"] = import.Heading;
                        facts["summary_head"] = Head(import.Summary);
                    }

                    Say($"import of [{string.Join(", ", names)}]: {run.Elapsed.TotalSeconds:N1} s (Begin + {(stopBefore is null ? "RunToEnd" : "stepping to the stop")}), failed={import.Failed}");
                    if (import.Failed)
                    {
                        outcome = "import-failed";
                    }
                    else if (stopOutcome != "ok")
                    {
                        outcome = stopOutcome;
                    }
                }

                facts["modifiable_after_import"] = document.IsModifiable;
                string save = Env("SAVE_CHECKPOINT");
                if (save.Length > 0 && outcome == "ok")
                {
                    if (document.IsModifiable)
                    {
                        throw new InvalidOperationException("The document is still modifiable after the import; a checkpoint with an open transaction is refused.");
                    }

                    Stopwatch saving = Stopwatch.StartNew();
                    document.SaveAs(save, new SaveAsOptions { OverwriteExistingFile = true });
                    facts["checkpoint_saved"] = save;
                    facts["checkpoint_save_seconds"] = Math.Round(saving.Elapsed.TotalSeconds, 2);
                    facts["checkpoint_bytes"] = new FileInfo(save).Length;
                    Say($"saved the checkpoint in {saving.Elapsed.TotalSeconds:N1} s ({new FileInfo(save).Length:N0} bytes)");
                }
            }

            document.Close(false);
        }
        catch (Exception ex)
        {
            outcome = "threw";
            facts["exception"] = ex.ToString();
            Say("THREW: " + ex);
        }

        facts["outcome"] = outcome;
        WriteResult(facts);
        Done(outcome);
    }

    private static string? Head(string? text) => text is null ? null : text.Length <= 600 ? text : text[..600] + "…";

    // ---- window phase ---------------------------------------------------------------------------

    private static void Watch()
    {
        // Give Revit's UI a moment after ApplicationInitialized before asking it to open a project.
        Thread.Sleep(3000);
        Say("raise open: " + _openEvent!.Raise());

        bool seenImporting = false;
        bool began = false;
        while (true)
        {
            Thread.Sleep(100);
            if (_openFailed)
            {
                return;
            }

            if (_active is not { } import)
            {
                continue;
            }

            if (!began && import.Staged is not null)
            {
                began = true;
                _beganAt = DateTime.Now;
                Say("Import was pressed: the plan is staged");
                File.WriteAllText(Path.Combine(_dir, "began.txt"), _beganAt.ToString("yyyy-MM-ddTHH:mm:ss.fff"));
            }

            bool importing = _handler!.IsImporting;
            seenImporting |= importing;
            if (seenImporting && !importing)
            {
                _endedAt = DateTime.Now;
                Finish(import, began);
                return;
            }
        }
    }

    private static void Finish(ActiveImport import, bool began)
    {
        Dictionary<string, object?> facts = new()
        {
            ["tag"] = Tag,
            ["source_commit"] = Meta("SourceCommit"),
            ["plugin_version"] = PluginVersion.Current,
            ["phase"] = _phase,
            ["pid"] = Environment.ProcessId,
            ["bundle"] = Bundle,
            ["import_began"] = began,
            ["began_at"] = began ? _beganAt.ToString("yyyy-MM-ddTHH:mm:ss.fff") : null,
            ["ended_at"] = _endedAt.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
            ["wall_seconds"] = began ? Math.Round((_endedAt - _beganAt).TotalSeconds, 2) : null,
            ["failed"] = import.Failed,
            ["heading"] = import.Heading,
            ["summary_head"] = Head(import.Summary),
            ["revit_name"] = _revitName,
            ["revit_build"] = _revitBuild,
            ["revit_version"] = _revitVersion,
        };
        facts["outcome"] = !began ? "dismissed-before-start" : import.Failed ? "import-failed" : "ok";
        Say($"the handler let go: began={began}, failed={import.Failed}, wall {(began ? (_endedAt - _beganAt).TotalSeconds.ToString("N1") : "-")} s");
        WriteResult(facts);
        Done((string)facts["outcome"]!);
    }

    private static string _revitName = "";
    private static string _revitBuild = "";
    private static string _revitVersion = "";

    private static void OpenForWindow(UIApplication ui)
    {
        try
        {
            RevitApplication app = ui.Application;
            _revitName = app.VersionName;
            _revitBuild = app.VersionBuild;
            _revitVersion = app.SubVersionNumber;
            Say($"Revit {app.VersionNumber} {app.VersionName} build {app.VersionBuild}, pid {Environment.ProcessId}; plugin {PluginVersion.Current}");
            (Document document, _) = OpenProject(app, ui);

            ActiveImport? import = ActiveImport.Open(app, document, Bundle, out ImportRefusal? refusal);
            if (import is null)
            {
                Say("REFUSED " + refusal?.Message);
                _openFailed = true;
                WriteResult(new Dictionary<string, object?> { ["tag"] = Tag, ["outcome"] = "refused", ["refusal"] = refusal?.Message });
                Done("refused");
                return;
            }

            _active = import;
            _handler!.TakeOver(import, ui.MainWindowHandle);
            File.WriteAllText(Path.Combine(_dir, "window-open.txt"), Now());
            Say("the import window is open on its checklist");
        }
        catch (Exception ex)
        {
            Say("OPEN THREW: " + ex);
            _openFailed = true;
            WriteResult(new Dictionary<string, object?> { ["tag"] = Tag, ["outcome"] = "threw", ["exception"] = ex.ToString() });
            Done("threw");
        }
    }

    private sealed class Handler(string name, Action<UIApplication> run) : IExternalEventHandler
    {
        public void Execute(UIApplication app) => run(app);

        public string GetName() => "timing " + name;
    }
}
