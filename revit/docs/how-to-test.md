# Mantle Place for Revit: how to test

A one-page guide for a store reviewer or a first-time tester of `revit-1.3.0`. It needs **no account**: the
sample bundle comes from a public page, and it is imported from disk. Signing in is needed only for the
vault and for Open in Curator's order extent, and both are optional below.

## 1. Install

1. Close Revit.
2. Install from the Autodesk storefront, or unzip `MantlePlace-Revit-1.3.0.zip` and run
   `MantlePlace.bundle\Install.cmd` (the manual copy is in `MantlePlace.bundle\README.txt`). Use one
   way in, not both.
3. Start Revit 2025.5 or later, 2026.5 or later, or 2027. The first time each Revit version loads the plugin it
   asks whether to load it: choose **Always Load**.
4. Create a new project from any template. Use a new project: an MPB 3.0 bundle is refused by a project that
   already holds a Mantle Place import. A **Mantle Place** tab appears on the ribbon, with two panels,
   **Account** and **Bundles**.

## 2. Download a sample bundle

Open [mantle.place/samples](https://mantle.place/samples) in a browser. The bundles there are free and
anonymous: no account, no email. Download **Austin Downtown** at its **Min** level, about 90 MB (Max and Med
import the same way). Do not unzip it.

Any bundle on that page imports. Austin Downtown is the one this guide describes: metres, about 2 km², and in
the United States, so its flood zones draw on the hazard plan.

## 3. Import it from disk

1. `Mantle Place ▸ Bundles ▸ Import Bundle`, and pick the zip.
2. The **Bundle Import** window opens. Under the bundle's name, one line names its units and coordinate system.
   Below it is a checklist headed **Include**, one box per layer, every box ticked but **Site Model** and
   **Road Subdivisions**, the road surfaces. The note under Road Subdivisions estimates them at over five
   minutes, which is why that box starts unticked; the notes under Planting and Context Buildings give their
   estimates too.
3. Leave the boxes as they are and press **Import**. Nothing runs before this.
4. The window lists each step and marks it `Waiting`, `Importing` and `Done`, counting the buildings and trees in
   as they go. On our test workstation the import took at most 8 minutes, in Revit 2026, and less in Revit 2025
   and 2027. Revit may say "not responding" while the terrain is committed; the window keeps its clock running
   and Revit comes back by itself. With every box ticked, road surfaces included, the same import took up to 32
   minutes.

## 4. What you should see

- A terrain (toposolid) about 1.4 km across, with aerial imagery draped on it, contours, and the site's
  buildings as context, each one its own element.
- Road centrelines and water on the terrain, and trees planted on it; no road surfaces, because their box was
  unticked.
- A 3D view named **Mantle Place Site Context**.
- Floor plans whose names begin **Mantle Place Hazard Plan**, **Mantle Place Land Use Plan** and **Mantle Place
  Land Cover Plan**, each with a key beside the site. A layer the area has none of draws no plan.
- The project placed at the coordinates the bundle publishes: Manage ▸ Location shows the site's latitude and
  longitude, in Austin, Texas, and the survey point is set.
- The window ends with every step `Done` and asks **Did this import come in right?**. `Yes` and `No` open a web
  form in the browser; `Dismiss` closes the question.

## 5. Each ribbon command, in order

**Account panel.** The face of the split button reads **Sign In**; its arrow opens the rest.

1. **Sign In** (optional). Opens your system browser at mantle.place, where you sign in or create a free account;
   the browser hands back to Revit and the face reads **Signed In**. Revit never shows a password field. Skip it
   to stay without an account.
2. **Sign Out**. Shown only while there is a session; drops it.
3. **Open mantle.place**. Opens the website in your browser.
4. **Send Feedback**. Opens the feedback form in your browser; it works signed out.
5. **About Mantle Place**. A window naming the plugin's version and the Revit it runs in, with a link to the
   privacy page and a **Logs** button that opens the folder holding the import's log.

**Bundles panel.**

6. **Vault**. Signed out, it says *Sign in first: Mantle Place ▸ Account ▸ Sign In.* Signed in, it lists the orders in your account; a new account
   has none. For an MPB 3.0 order it shows **Fidelity Level** with Max, Med and Min, each with its download size.
7. **Import Bundle**. Step 3 above.
8. **Prepare for Handover**. Asks first, then removes the open-licence (ODbL) map content the import brought in
   and keeps the data credit. It is one undo step: press Ctrl+Z to put it back.
9. **Open in Curator**. Opens mantle.place in your browser, where sites are drawn and ordered. After the sample import it asks for a size
   (500 m to 4 km) around the project's site location, then opens the curator on that area. It changes nothing in
   the project.

The arrow at the foot of the Bundles panel opens two more:

10. **Probe Terrain**. Measures what a terrain import would find in this project and rolls every attempt back;
    nothing changes. It ends with *Terrain probe finished. Nothing was changed.* and where it wrote its measurements.
11. **Logs**. Opens File Explorer on the newest import or probe log.

**F1** over any Mantle Place button opens the plugin's help page. Every plugin window shows the Mantle Place
mark, or no logo; none shows the Revit logo.

## The sample bundles

The samples page states each bundle's sources and licences, and each bundle's own `README.md` and manifest
carry the full attribution.
