# Revit 1.3.0: the store listing

The text of the Autodesk storefront listing for `revit-1.3.0`, one section per text field of the publisher's
form, in the form's order. Each field is pasted as it stands, without the heading. Each heading gives the
field's name on the form, the count of the text below it (spaces included) and the form's own limit, read from
the form on 2026-11-19. It describes only what 1.3.0 does: what it changed is
[`release-notes-1.3.0.md`](release-notes-1.3.0.md), and the steps the store's reviewer follows are
[`how-to-test.md`](how-to-test.md).

Two rules from the store's earlier reviews hold for every field and every image: the title does not name the
host product, because the storefront allows that only for an app listed for more than one product, and no
listing image or plugin window shows the host product's logo. The icon is the Mantle Place mark, the
company logo ([ADR 0033](../../docs/adr/0033-every-host-shows-the-round-mark-with-its-extrude.md)).

Fields that are not text are set on the form itself: **Autodesk Product Compatibility** is Revit 2025, 2026
and 2027; **Price** is Free, and follows the storefront's answer on the pricing category: the founder has asked
the storefront to keep the listing Free on the strength of the description's paragraph on what is free and
what is bought, and sets Trial if the storefront still requires it, with no change to the text; **Learn More Url** is `https://mantle.place/revit`. The **Commands** table is left
empty: it takes a command typed at a command line, and Revit has none.

## App Name (31 of 50 characters)

```text
Mantle Place — Real-World Sites
```

## App Short Description (148 of 200 characters)

```text
Import a real site into your project from a Mantle Place bundle: terrain, buildings, roads, trees and imagery, placed at its real-world coordinates.
```

## App Description (1,880 of 4,000 characters)

```text
Mantle Place brings the real world around your site into your project. Draw an area on mantle.place, order it, and import the bundle here: the plugin builds the ground and the context around it, and places all of it at the site's real coordinates.

What an import brings in, each as a box you can untick:
- Terrain, as a toposolid, with aerial imagery draped on it
- Published contours
- Context buildings, each its own element you can select, hide or colour, and the site model as a link
- Road centrelines, road surfaces and water
- Trees and shrubs, planted on the ground
- Land use and land cover, each on a plan of its own
- Flood zones and steep ground, on a hazard plan that never changes the terrain

The import sets the project's shared coordinates and site location from the bundle, so the sun, the survey point and every later bundle line up. A second bundle ordered beside the first is placed edge to edge with no manual move. Importing the same bundle again reuses what it built.

The import window shows each step as it runs and can be cancelled. You choose what to bring in before anything runs, and for a large layer the window says how long it will take first.

Sign in to reach your vault: list your orders, download one at the level you choose, and import it. Send a bundle from the web vault and Revit opens it for you. Open in Curator takes you from the project to the web curator on the same site.

A bundle you already have imports from disk with no account.

The plugin is free, with no time limit and no locked commands. Site bundles are ordered on mantle.place, where each site's price is shown before you buy; Open in Curator opens that page in your browser. Free sample bundles are at mantle.place/samples.

Prepare for Handover removes the open-licence map content an import brought in before you hand a project to a client, and keeps the data credit.
```

## Version Description (1,094 of 4,000 characters)

```text
- Road surfaces from an MPB 3.0 order import: a download from the vault now asks for the road surface file.
- Road and water surfaces are cut in batches, polygons near each other in one subdivision, so an import of a thousand roads no longer takes most of an hour.
- The vault and import windows list only what an order includes, with no row for an Extra the order does not include and no wording about paid sites.
- Open in Curator says in its tooltip where it goes: mantle.place in your browser, where sites are drawn and ordered.
- The round Mantle Place mark is on the ribbon buttons, the window title bars and the bundle's icon.
- Sign In refuses a configuration that splits it across two servers, naming both, and names a server that does not answer.
- A layer in another coordinate system than the origin's is named as left out, and a re-import of a bundle on a rotated grid is placed at a zero offset instead of being refused.

An MPB 3.0 bundle goes into a project that holds no earlier Mantle Place import; a project that already holds one refuses it, says why, and is left unchanged.
```

## General Usage Instructions (776 of 1,000 characters)

```text
1. Get a bundle. Order a site on mantle.place, or download a free sample bundle from mantle.place/samples (no account needed).
2. Import it. Create a new project, then Mantle Place > Bundles > Import Bundle and pick the zip, or sign in and use Vault to download an order and import it. The Bundle Import window lists every layer as a box; untick what you do not want and press Import.
3. Watch it run. Each step is marked as it runs and can be cancelled. The terrain, buildings, roads, water and trees go into the project at the site's real coordinates, with a Mantle Place Site Context 3D view and the land and hazard plans.
4. Hand it over. Prepare for Handover removes the open-licence map content and keeps the data credit.

Press F1 over any Mantle Place button for help.
```

## Installation/Uninstallation (735 of 1,000 characters)

```text
Requirements: Revit 2025.5 or later, Revit 2026.5 or later, or Revit 2027, on Windows 64-bit; one build serves all three. Revit 2025 and 2026 need their .5 update. Importing a bundle from disk needs no account; the vault needs a free Mantle Place account.

Install: close Revit, install from this store, and start Revit. The first time each Revit version loads the plugin it asks once whether to load it: choose Always Load. A Mantle Place tab appears on the ribbon. If you installed Mantle Place from the zip on GitHub before, run its Uninstall.cmd first, so the plugin does not load twice.

Uninstall: close Revit and uninstall Mantle Place from Windows Settings > Apps > Installed apps. Your projects keep everything an import made.
```

## Support Information (448 of 1,000 characters)

```text
Press F1 over any Mantle Place button to open the plugin's help page. To report a problem or ask for help, use Mantle Place > Account > Send Feedback, which opens mantle.place/feedback in your browser; it needs no account. Mantle Place > Account > About Mantle Place names your plugin and Revit versions, and its Logs button opens the import log to attach. The plugin's source and documentation are public at github.com/mantleplace/mantleplace-dcc.
```

## Known Issues (344 of 1,000 characters)

```text
An MPB 3.0 bundle cannot be imported into a project that already holds a Mantle Place import; import it into a new project. Some items a 2.x bundle brings in are not part of an MPB 3.0 import yet: the time zone, cropping the terrain to the area of interest, and some details on the hazard plan's key. Each is listed as skipped, with its reason.
```
