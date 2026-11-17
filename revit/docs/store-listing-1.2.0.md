---
name: revit-store-listing-1-2-0
description: Draft Autodesk storefront listing text for Mantle Place for Revit 1.2.0 - title, short and long description, what is new, and technical details. Read before editing the storefront listing or cutting revit-1.2.0; it describes only what 1.2.0 does, and the founder approves every word.
---

# Storefront listing text, Revit 1.2.0

**Draft for the founder's approval.** Each field below is the text to paste into the matching storefront
field. It describes only what the plugin does at 1.2.0; where the README or the release notes
([`release-notes-1.2.0.md`](release-notes-1.2.0.md)) own a fact, this file words it for a store page and
does not become a second home for it. The storefront's field limits are not recorded in this
repository: check each field's length in the submission form before pasting, and where a limit differs
from this file, the form wins. The steps for the store's reviewer are in
[`store-review-1.2.0.md`](store-review-1.2.0.md).

The storefront's earlier review asked for no host product name in the title and no host logo. The title
below carries neither, and no image in the listing should carry the host's logo.

## Title

Mantle Place - Real-World Terrain, Imagery, Buildings, Roads and more

## Short description

Bring a real site into a project: terrain, aerial imagery, buildings, roads, trees and more, from a
bundle you ordered or downloaded. A bundle import needs no account.

## Long description

Mantle Place turns open geospatial data for any site into a bundle: one download holding the terrain,
aerial imagery, buildings, roads, land cover and more for the area you chose. This plugin brings that
bundle into Revit, placed at the site's real coordinates.

**One ribbon tab.** A **Mantle Place** tab holds the commands: Import Bundle, Vault, Prepare for
Handover, Open in Curator and, in the panel's slide-out, Probe Terrain and Logs. The Account button on the
same tab signs you in through your browser.

**What an import brings in.** A **Bundle Import** window opens on a checklist headed **Include**, one row
for each layer the bundle carries, and nothing runs until you press **Import**:

- Terrain as a toposolid, with the aerial imagery draped on it.
- Published contours.
- Context buildings, each its own Generic Model element, and the site model linked from its IFC.
- Road centrelines, and road and water surfaces.
- Trees as planting.
- Land use and land cover on floor plans of their own.
- Flood zones and steep ground on a hazard plan.
- The project's shared coordinates and site location, set from the bundle, and the data credit in an
  attribution view.
- And more, depending on what the bundle holds.

**Watch it work.** The window lists each step with its state (Waiting, Importing, Done, Failed,
Cancelled or Not Run) and a Cancel that is honoured between steps. A layer you leave unticked is left
out and the log says so. Importing the same bundle again reuses what it built and adds only what is
missing.

**MPB 3.0 beside 2.x.** The plugin reads MPB 3.0 bundles and MPB 2.x bundles side by side; a 2.x bundle
imports as it did before. A step whose file a 3.0 bundle does not hold is shown as skipped, with the
file's name and the reason.

**Choose a level before you download.** For an MPB 3.0 order in your vault, the Vault window shows a
**Fidelity Level** control with Max, Med and Min, each with its download size and the platform's
recommendation preselected, so you download only the level you want. In the import window, a 3.0 row
that would take more than about five minutes starts unticked with its estimate; tick it and all of it
imports.

**Your vault, in Revit.** Sign in once in your browser. The Vault window lists the bundles you keep,
prepares their Revit files, downloads them and imports them, and Revit stays usable while it works. A
notice tells you when a bundle is ready.

**Open in Curator.** Opens the web curator in your browser on the extent your project already knows, to
order the next area.

**Sharing a project.** Content made from OpenStreetMap-derived data is kept in selection filters of its
own, and **Prepare for Handover** removes it, leaves the credit text in the project and is one undo step.

**Yours to keep.** The import is ordinary Revit elements in your project.

## What is new in 1.2.0

- Imports MPB 3.0 bundles, beside MPB 2.x ones.
- Choose Max, Med or Min before an MPB 3.0 order downloads, with each level's size.
- For an MPB 3.0 bundle, each import row says what it will take, and a row that would take more than
  about five minutes starts unticked.
- A 3.0 step whose file the bundle does not hold is shown as skipped, with the reason.

## Technical details

- Revit 2025.5 or later, Revit 2026.5 or later, and Revit 2027, from one build.
- Windows 64-bit. The plugin installs for your Windows account and needs no administrator rights.
- Network: signing in and the vault need an internet connection; importing a bundle from disk does not.
- Install from the storefront, or unzip the release package and run `Install.cmd`. Use one way in, not
  both.
- Distribution: a `.bundle` package with the add-in files and an `Install.cmd` and `Uninstall.cmd`.
