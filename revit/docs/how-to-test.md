# Mantle Place for Revit: how to test

A one-page guide for a store reviewer or a first-time tester. Every command on the plugin's ribbon tab
can be exercised with the sample bundle attached to the release; signing in is needed only for the
vault and for Open in Curator's order extent.

## 1. Install

1. Close Revit.
2. Install from the Autodesk storefront, or unzip `MantlePlace-Revit-<version>.zip` and run
   `MantlePlace.bundle\Install.cmd` (the manual copy is in `MantlePlace.bundle\README.txt`). Use one
   way in, not both.
3. Start Revit 2025.5 or later, 2026.5 or later, or 2027 (see [Supported hosts](../README.md)) and open or create any project. A **Mantle Place** tab appears on the
   ribbon.

## 2. Sign in (optional)

`Mantle Place ▸ Account ▸ Sign In` opens your system browser at mantle.place. Sign in or create a free
account there; the browser hands back to Revit and the button reads **Signed In**. Revit never shows a
password field.

## 3. Import the sample bundle from disk

1. Download `MantlePlace-Sample-Bundle.zip` from the same release. Do not unzip it.
2. `Mantle Place ▸ Bundles ▸ Import Bundle`, and pick the zip.
3. The **Bundle Import** window opens on a checklist headed **Include**. Leave the defaults and press
   **Import**. The window lists each step as it runs; it takes about a minute.

## 4. What you should see

- A terrain (toposolid) about 1 km across, with aerial imagery draped on it and the site's buildings
  as context.
- The project placed at the coordinates the bundle publishes: its survey point and project location
  are set by the import.
- The window ends with every step finished. `Mantle Place ▸ Account ▸ About Mantle Place` names the
  build and Revit year, and offers **Open the import logs**.

## 5. The other commands

- **Vault** (signed in): lists the bundles in your account; empty for a new account.
- **Open in Curator**: opens the web curator in your browser on the project's extent. After the sample
  import, unless the sample's order is in your vault, it asks for a size (500 m to 4 km) around the
  project's site location; it changes nothing in the project.
- **Account ▸ Open mantle.place**, **Send Feedback**: open the website in your browser.
- **F1** over any button opens the plugin's help.

Every plugin window carries the Mantle Place mark in its title bar.

## The sample bundle

About 1 km² near Jackson, Wyoming. Imagery: USDA NAIP; elevation: USGS 3DEP (both public domain).
Basemap and vector data © OpenStreetMap contributors, ODbL 1.0. The bundle's own manifest carries the
full attribution.
