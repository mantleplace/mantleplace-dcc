# Real-Revit timing harness

Times one commit's Revit plugin code in a real Revit, quietly, beside the installed plugin and without
touching it. It builds the commit's sources into a renamed scratch assembly, launches a Revit of its own
(no mouse, no keyboard, no focus steal, no prompt left for a person), runs one import of a bundle zip, and
leaves the plugin's log, Revit's journal and a `timings.md` in a run folder. A second script turns several
run folders into one cross-run step table.

It is a developer tool for this machine's Revit, not part of the plugin and not run by CI: no hosted
runner has Revit, and none may (see the root `CLAUDE.md`). Only `test_summarise_runs.py` runs without
Revit.

| File | What it does |
|---|---|
| `Export-TimingSource.ps1` | `git archive` of `revit/` at one commit into the work root, with a `COMMIT` file |
| `Build-TimingHarness.ps1` | compiles that source plus `Harness/` into one assembly named for a tag |
| `Invoke-TimingRun.ps1` | the driver: launches Revit, runs one import, collects the record |
| `Harness/TimingHarness.cs` | the add-in loaded into that Revit; it does nothing unless launched for its own tag |
| `lib/` | the Win32 and UI Automation helpers the driver dot-sources, and the work-root rule |
| `timings.py` | one run folder → `timings.md` (the driver calls it at the end of every run) |
| `summarise_runs.py` | several run folders → one step × run table, with the spread between runs of a year |

**Needs:** Windows, PowerShell 7 (`pwsh`), the .NET SDK the plugin builds with, Python 3, Revit 2025
installed (the compile target, whatever year you run), and the Revit year you run.

## Quick start

```powershell
cd revit/tools/timing
$env:MANTLEPLACE_TIMING_BUNDLE = 'C:\path\to\bundle.zip'      # or pass -Bundle on every run

# the default boxes, no window, in Revit 2025, at commit d4f3453
./Invoke-TimingRun.ps1 -Phase layers -Tag base-d4f3453 -Year 2025 -Commit d4f3453 -Layers Default

# every box, through the import window a curator gets, in Revit 2027
./Invoke-TimingRun.ps1 -Phase window -Tag base-d4f3453 -Year 2027 -Boxes All -TimeoutSeconds 7200

python summarise_runs.py "$env:LOCALAPPDATA\MantlePlaceTiming\runs\base-d4f3453-2025" `
                         "$env:LOCALAPPDATA\MantlePlaceTiming\runs\base-d4f3453-2027"
```

### Pointing it at things

| What | Parameter | Environment variable | Default |
|---|---|---|---|
| The bundle zip to import | `-Bundle` | `MANTLEPLACE_TIMING_BUNDLE` | none: one is required |
| Where everything it makes goes | `-WorkRoot` | `MANTLEPLACE_TIMING_ROOT` | `%LOCALAPPDATA%\MantlePlaceTiming` |
| The Revit year to run | `-Year` (2025, 2026, 2027) | | required |
| The code to time | `-Commit <sha>`, or `-SourceRoot <dir with revit\src>` | | needed the first time a tag is built |
| Where Revit is installed | `-RevitRoot` (holds `Revit <year>\Revit.exe`) | | `%ProgramFiles%\Autodesk` |
| The compile target | `-RevitApiDir` on `Build-TimingHarness.ps1` | | `%ProgramFiles%\Autodesk\Revit 2025` |
| Where windows are parked | `-ParkScreen \\.\DISPLAYn` | | the smallest non-primary screen; off-screen with one screen |

The work root is refused if it lies inside this repository: a run folder holds a copy of the bundle (hundreds
of MB), and this tree may be mounted inside a consuming project whose tools scan it. Under the work root:
`src\<sha7>\` (exports), `builds\<tag>\` (builds), `runs\<tag>-<year>\` (run folders), `checkpoints\<year>\`
and `pids\`.

### Choosing layers or boxes

- **Layers phase** (`-Phase layers -Layers ...`): the importer with no window. `-Layers` takes the names of
  `ImportLayer` in `revit/src/MantlePlace.Revit.Core/ImportLayers.cs` of the source being timed (`Terrain`,
  `PublishedContours`, `ContextBuildings`, ...; spaces ignored), `All`, or `Default` (the layers
  `ImportLayers.OnByDefault` ticks, which is what a curator who changes nothing imports). The driver checks
  the names against that enum before launching.
- **Levels, layers phase only** (`-Levels ...`): the fidelity level each layer imports at. `Default` is
  `ImportLayers.DefaultLevel`, what the window opens on; `All=Min` puts every layer at one level; and a
  list such as `'All=Min,Planting=Med'` is read left to right, so a later entry wins. Empty means every
  layer at `MAX`, except with `-Layers Default`, which takes the default levels too. A level the bundle
  publishes in a form the step cannot take is refused by the planner and logged, as the window would
  never offer it.
- **Window phase** (`-Phase window -Boxes ...`): the real import window. `-Boxes` takes `All` (every box
  the bundle offers), `Default` (the boxes as the window opens them, untouched), or a comma list of the
  checklist's row names (`'Published Contours,Terrain'`); boxes not named are unticked. The levels are
  the ones the window opens on; the driver does not change them.

Pass a comma list quoted (`-Layers 'Terrain,PublishedContours'`) when calling from a PowerShell session; an
unquoted list arrives as an array and is refused.

## The rules that keep runs apart

- **One Revit at a time, any year.** Before launching, the driver lists every running Revit (its pid, year
  and title, and whether a run of this harness launched it) and refuses while any is up, so a timing never
  measures contention and a running Revit never picks up this run's temporary add-in manifest.
  `-WaitForRevitSeconds N` waits up to N seconds for them to exit instead. The check is made again under
  a machine-wide launch lock just before anything is moved or written, so two drivers starting in the same
  minute cannot both pass.
- **A tag names one source.** Pick your own, letters, digits, `-` and `_`, 31 characters at most. The tag
  decides the assembly name (`MantlePlace.Scratch.Timing.<tag>`), the add-in ClientId, the build folder and
  the run folders. Building an existing tag from another source is refused (`-Force` / `-Rebuild` on a tag
  that is yours alone). If you edit `Harness/TimingHarness.cs`, rebuild every tag you still use.
- **The installed plugin slot is never read or written.** The run's temporary manifest is
  `MantlePlace.Timing.<tag>.addin` in `%APPDATA%\Autodesk\Revit\Addins\<year>\`, written just before the
  launch and deleted as soon as the harness reports `started.txt` (or when the run ends, however it ends).
  The harness loads from a per-run copy of the build. If a run dies hard and leaves a manifest behind:
  `Remove-Item $env:APPDATA\Autodesk\Revit\Addins\*\MantlePlace.Timing.*.addin`. A leftover is harmless:
  the harness does nothing unless `MANTLEPLACE_TIMING_TAG` equals the tag compiled into it, and logs the
  refusal to `%TEMP%\mantleplace-timing-declined.log`.
- **Run folders are never wiped.** A second run of the same tag and year first moves the earlier folder to
  `runs\<tag>-<year>.prev-<time>`.
- **Each run imports a copy of the bundle**, so the plugin's `<zip>.mantleplace-import.log` never lands
  beside the zip you passed. The copy, the project copy and the add-in copy are deleted when the run ends
  (`-KeepFiles` keeps them). The plugin itself still extracts to
  `%LOCALAPPDATA%\MantlePlace\bundles\<order id>\extracted`, keyed by the manifest's order id, so every
  run of an order and a real import of it share that folder.
- **Quiet by construction.** Revit is created through WMI minimised without activation (a child of the
  foreground terminal would inherit the right to take focus); its windows are parked on `-ParkScreen` without
  activation; the "Security - Unsigned Add-In" prompts (the harness, and the installed plugin when it is
  newly deployed) are answered with `TDM_CLICK_BUTTON` to the dialog window; the import window is driven by
  UI Automation Toggle and Invoke only. Nothing moves the mouse or sends a key; `driver.txt` records the
  cursor and the foreground window before and after, and hands the foreground back if Revit takes it. Only
  the pid the driver launched is ever stopped.

## Source and build

```powershell
./Export-TimingSource.ps1 -Commit d4f3453          # prints the export's path
./Build-TimingHarness.ps1 -Tag base-d4f3453 -SourceRoot <that path>
```

`Invoke-TimingRun.ps1 -Commit` does both when the tag has no build yet. `-SourceRoot` may also be a git
worktree; the commit is read from it, with `-dirty` appended when `revit/` has changes. The commit is
recorded in `build.json`, `meta.json`, `driver.txt`, `timings.md` and, because the informational version
carries it, the plugin log's first line. The tag is compiled into the assembly as a constant, read back out
of the finished DLL, and the build is refused if it differs.

The harness calls the plugin's own import types (`ActiveImport`, `ImportLayerChoice`, `StagedImport`,
`BundleImportEventHandler`). A commit that renames one of them fails to build here; fix
`Harness/TimingHarness.cs` in the same change.

## The IFC link runs cold by default

The Site Model step converts `Site\Site.ifc` into `Site\Site.<year>.rvt` in the shared extracted folder only
when that file is missing, so a second run of a year would reuse the first's conversion. With `-ColdIfc` (on
by default; `-ColdIfc:$false` turns it off) the driver moves exactly that one file into the run folder
before launch, after proving the copy has the same sha256 and writing `ifc-cache.json`, and puts it back
byte-identical when the run ends, however it ends. If the restore fails it says so loudly in `driver.txt`,
`result.json`, `meta.json` and `timings.md`, leaves the original in the run folder's
`ifc-cache-original\`, and refuses every later cold run of that order until the original is put back. It
acts only when the run imports the Site Model layer.

## Window phase

A fresh project is opened by an ExternalEvent, and the real `ActiveImport.Open` plus
`BundleImportEventHandler.TakeOver` show the real import window on its checklist. The driver reads every
box by UI Automation, sets them to `-Boxes`, reads them back, and presses Import. From the press it appends
one tab-separated line to `window.txt` every `-SampleSeconds` (5):

`elapsed_s  clock  current_step  status_line  states  progress_bar  close_button  hung_main  hung_window  uia_ms  stale_s  log_step  note`

The window can only be read between slices: a long commit keeps Revit's UI thread away from it, so the
readings run on a thread of their own, and each line carries the newest completed reading with its age
(`stale_s`). `log_step`, read from the plugin log, is always current. The run ends when the import window
lets go; `window-final.txt` holds its final reading and report. `-Minimize` minimises Revit once Import is
pressed.

## Layers phase

`ImportLayerChoice.Only([...])` then `Begin` and `RunToEnd`, on a new project or on a copy of
`-OpenCheckpoint`. Layers that need the terrain need it in the project: use a checkpoint that holds it.

- `-SaveCheckpoint <name|path>` saves the project after the import, every transaction closed, to
  `checkpoints\<year>\<name>.rvt` with a `<name>.json` sidecar (commit, layers, bundle hash, seconds). It
  refuses to overwrite one (`-OverwriteCheckpoint`). A checkpoint opens only in the Revit year that saved it.
- `-OpenCheckpoint <name|path>` works on a copy of a checkpoint.
- `-StopBefore <ImportLayer>` plans with the whole `-Layers` choice (so the plan is the one a full import
  runs), steps the staged import one slice at a time, and stops in front of the first step that builds the
  named layer: every earlier step ended, the named one not started, no transaction open. Combined with
  `-SaveCheckpoint` it keeps the project exactly as a full import leaves it at that point. It never calls
  the import's `Finish()` (which commits terrain smooth shading). `stop-before.txt` records which steps ran.

## What a run leaves

In `runs\<tag>-<year>\`:

| File | What |
|---|---|
| `timings.md` | one row per step: start, end, seconds, journal commit seconds, outside-commit seconds, largest commit; then where the outside time went, the commits, a journal-vs-log cross-check, and the window's step changes. Rebuild with `python timings.py <run dir>` (`--out other.md` leaves `timings.md` alone) |
| `import.log` | the plugin's log, copied from beside the zip copy |
| `journal.txt`, `journal-source.txt` | the Revit journal of that process, and how it was chosen |
| `window.txt`, `window-tree-*.txt`, `window-final.txt` | window phase: the readings, the UI Automation tree, the final reading |
| `meta.json`, `result.json` | the driver's record (arguments, commit, timestamps, outcome, the IFC link), and the harness's (import wall seconds, Revit build, failure) |
| `driver.txt`, `harness.txt` | what the driver did (prompts answered, windows parked, focus); what the harness did inside Revit |
| `ifc-cache.json`, `ifc-cache-original\`, `ifc-cache-run\` | only when `-ColdIfc` acted: the record, the original while it is out of the cache, and the run's own conversion |
| `stop-before.txt` | only with `-StopBefore` |
| `timeout.txt` | only after a cutoff |

**How `timings.md` splits a step's time.** A commit's seconds are the journal's own, from the
`EndOrAbortUndoTransaction();DOPT;` line, named from `"Transaction Successful", "<name>"`. Revit prints no
timing for a commit under about 0.05 s; those show as `<0.05`. A commit is placed in a step by matching it to
the plugin log's `commit took` line (same name or none, seconds within 0.15 s plus 0.1 %, end time within
about a second and a half); anything unmatched is placed by time. A log stamp is a whole floored second, so
a step ends at the end of the second its `(Kind took N s.)` line carries. Everything in the step that is not
a commit is "outside commits": the plugin's own work and the Revit calls it makes before committing.

The import's wall clock is `result.json` `wall_seconds`, from the harness opening the import to the import
ending (window phase: from the Import press). `meta.json` `elapsedSeconds` counts from launching Revit, so it
includes Revit's start-up (15 to 70 s).

## The cross-run table

```powershell
python summarise_runs.py <run dir> <run dir> ... [--out table.md]
```

One row per step (the plugin log's `(Kind took N s.)` seconds), one column per run, then the import's wall
clock and the launch-to-end time. When two or more runs share a Revit year, a `spread <year>` column gives
the largest minus the smallest figure of that year's runs. A difference between two commits smaller than
the spread between runs of one commit is not a measured difference. Its cases:
`python -m unittest test_summarise_runs` from this folder.

## Cutoff

`-TimeoutSeconds` counts from launching Revit. At the cutoff the driver writes `timeout.txt` (elapsed time,
the step the plugin log was in, the last window reading, the journal's last 30 lines), copies the log and
journal, and stops the pid it launched. Outcome `timeout` is a measurement, not a failure; the in-flight step
shows as `(cut off)` in `timings.md`. Outcome `revit-exited` is Revit ending by itself (a crash; see the
journal).

## Traps met building this

- Revit's journal depth differs by context: a transaction run from an ExternalEvent (window phase) is depth
  1, from `ApplicationInitialized` (layers phase) depth 2. Anything that greps `1:<<EndOrAbort...` misses one.
- The first UI Automation call while a slice runs can block for several seconds; the driver reads only when
  `IsHungAppWindow` is false, and takes a sample's clock before the read.
- `window-open.txt` is the harness's marker that the window is up; `window.txt` is the driver's readings.
- Every number the driver writes for a machine to read is invariant-culture with no group separator.
  `timings.py` still accepts `2,754.0` from older run folders. `harness.txt` uses the session culture;
  nothing parses it beyond one `Revit ... build ..., pid` line.
- **A sandboxed shell can hide the build from Revit.** Revit is created by WMI, outside whatever shell ran
  the driver. A shell whose writes are redirected (an agent sandbox that virtualises `%LOCALAPPDATA%`, for
  one) leaves the build where Revit cannot see it, and Revit reports "Add-in Assembly Not Found" for a file
  that the shell can list. Point `MANTLEPLACE_TIMING_ROOT` at a folder that shell writes through to.
- A driver killed outright (`Stop-Process` on the pwsh, power loss) cannot run its `finally`: the temporary
  manifest may stay behind (harmless, see above), and after a cold run `ifc-cache.json` says where the
  IFC companion's original is. A plain Ctrl-C does run the `finally`.
