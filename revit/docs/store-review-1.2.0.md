---
name: revit-store-review-1-2-0
description: Draft instructions for the Autodesk storefront's reviewer of Mantle Place for Revit 1.2.0 - a no-account walk-through from the public samples page, each ribbon command in order with what to expect. Read before submitting revit-1.2.0 to the storefront; the founder approves every word.
---

# Reviewer instructions, Revit 1.2.0

**Draft for the founder's approval.** Paste into the storefront's field for reviewer notes. Everything
here works without an account: the sample comes from a public page and is imported from disk. The
listing text is in [`store-listing-1.2.0.md`](store-listing-1.2.0.md); the longer first-time guide is
[`how-to-test.md`](how-to-test.md).

## Before you start

1. Install the plugin from the storefront (or unzip the release package and run
   `MantlePlace.bundle\Install.cmd`; use one way in, not both).
2. Start Revit 2025.5 or later, Revit 2026.5 or later, or Revit 2027, and open or create any project.
   A **Mantle Place** tab appears on the ribbon with two panels, **Account** and **Bundles**.
3. Open `https://mantle.place/mpb-samples` and download one of the sample bundle zips. Any one will do;
   the steps are the same. **Do not unzip it.**

## The commands, in order

**1. Bundles > Import Bundle.** A file picker titled *Choose a Mantle Place bundle* opens. Pick the zip.
The **Bundle Import** window opens: the bundle's name, a line saying its units and coordinate system,
and a checklist headed **Include** with a row for each layer the bundle carries. Leave the defaults and
press **Import**. The window lists each step, marking it Waiting, Importing, Done, Failed, Cancelled or
Not Run, and it can take a few minutes. Revit stays responsive between steps, and **Cancel** stops the
import at the next step. When it ends, the window asks "Did this import come in right?"; press
**Dismiss**.

What you should see in the project: a terrain (a toposolid) with the aerial imagery draped on it, the
site's buildings as context, and the project's survey point and site location set by the import.
Layers the bundle carries but you did not tick, such as the linked site model, are left out and the log
says so. A step the bundle has no file for is shown as skipped, with the reason.

**2. Bundles > Open in Curator.** With no account, a dialog asks *How much around the project's site
location?* and offers sizes. Pick one. Your browser opens the web curator on that area. Nothing in the
project changes.

**3. Bundles slide-out > Logs.** Open the panel's slide-out with the small arrow at the foot of the
**Bundles** panel. **Logs** opens File Explorer with the newest import log selected. Every import writes
its log beside the zip it was given.

**4. Bundles slide-out > Probe Terrain.** A file picker titled *Choose a Mantle Place bundle to probe*
opens. Pick the same zip. When it ends, a dialog reads *Terrain probe finished. Nothing was changed.* and
names the file holding the measurements. Every attempt is rolled back, so the project is untouched.

**5. Account > About Mantle Place.** Signed out, the Account button reads **Sign In**; open its dropdown
and choose **About Mantle Place**. The window names the plugin's build and the Revit it is running in,
has a **Logs** button that goes to the same place as step 3, and links the privacy page.

**6. Account > Open mantle.place, and Send Feedback.** Each opens a page in your browser. The feedback
form needs no account.

**7. Bundles > Prepare for Handover.** Run it last: it removes the content the import made from
OpenStreetMap-derived data. A dialog asks *Prepare this project for handover?*; choose **Yes**. A dialog
then reads *This project is prepared for handover.* and says how much was removed. It is one undo step
and saves nothing, so **Ctrl+Z** puts it back.

**F1** over any button on the tab opens the plugin's help page.

## Two commands that need an account

**Account > Sign In** opens your system browser at mantle.place, where you sign in.
Once signed in the button reads **Signed In** and **Bundles > Vault** lists the bundles in your account
(none, for a new account). Ordering a bundle needs an account, and the review does not require it.

The Vault is also where the **Fidelity Level** control appears: for an MPB 3.0 order it offers Max, Med
and Min with each download size, before the download. The samples page serves MPB 2.0 bundles, which the
plugin imports through the steps above, so this control and a 3.0 import need an order of your own to
see.

## The sample bundles

Each sample carries OpenStreetMap-derived data under the ODbL; every such file holds its notice and the
licence link. Keep the files together if you pass them on.
