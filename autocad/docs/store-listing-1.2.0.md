---
name: autocad-store-listing-1-2-0
description: Draft Autodesk storefront listing text for Mantle Place for AutoCAD 1.2.0, covering AutoCAD and Civil 3D - title, short and long description, what is new, known issues, support and technical details. Read before editing the storefront listing or submitting autocad-1.2.0; it describes only what 1.2.0 does, and the founder approves every word.
---

# Storefront listing text, AutoCAD 1.2.0

**Draft for the founder's approval.** This is the plugin's first storefront submission, and one listing
covers AutoCAD and Civil 3D: they are one plugin. Each section below is the text to paste into the
matching storefront field. It describes only what the plugin does at 1.2.0; where the README
([`../README.md`](../README.md)) or the release notes ([`release-notes-1.2.0.md`](release-notes-1.2.0.md))
own a fact, this file words it for a store page and does not become a second home for it. The steps for
the store's reviewer are in [`store-review-1.2.0.md`](store-review-1.2.0.md).

**Field limits held to.** Autodesk's publisher guide for its app store (*Browser based app submission
process overview*, undated) gives the App Description 4000 characters, Known Issues 1000, Support
Information 1000 and Additional Information 2000, with minimal HTML. It states no limit for the app name,
the short description or the version description. The text below fits those limits; where the
submission form shows a different one, the form wins. The same guide says "AutoCAD" in the compatibility
list means plain AutoCAD only, so Civil 3D is ticked as a product of its own.

No host logo appears in the listing or in any image submitted with it; the plugin's buttons carry the
Mantle Place monogram and no other logo.

## App name

Mantle Place - Real-World Terrain, Contours, Imagery, Parcels and more

## Short description

Bring a real site into a drawing: contours, aerial imagery, parcels and zoning, site linework and more,
and in Civil 3D the terrain as a TIN surface, from a bundle you ordered or downloaded. A bundle import
needs no account.

## App description

Mantle Place turns open geospatial data for a site into a bundle: one download holding the terrain,
aerial imagery, contours, public records and more for the area you chose. This plugin brings that bundle
into the active drawing in AutoCAD or Civil 3D, at the order's own coordinates and in its own units.

**What an import brings in.**

- Contours, as polylines, each at its published height.
- The aerial image, at the position and size the bundle publishes, as an image the drawing links to.
- Public-record layers, such as parcels and zoning, as polylines on a layer per record.
- Site linework, as polylines on a layer per drawing layer.
- In Civil 3D, the terrain as a TIN surface built from the order's LandXML.
- And more, depending on what the bundle holds.

Everything goes on layers that start with `MantlePlace-`, so it is easy to find, freeze or remove.

**Watch it work.** Every import runs in a Bundle Import window that lists its steps (Verify Files first,
then each part the order places) with each one's state, the step in flight and an Elapsed clock. Cancel
stops the import between steps. When it is over, the window and the command line say what went in, and
what did not and why. Every file is checked against its published digest before anything is drawn.

**MPB 3.0 beside 2.x.** The plugin reads MPB 3.0 bundles and MPB 2.x bundles side by side. A part a 3.0 bundle does not hold is shown as skipped, naming the file, with the
bundle's reason where it gives one.

**Choose a level before you download.** For an MPB 3.0 order, the level control offers Max, Med and Min,
each with its download size and the platform's recommendation preselected: in the Vault window before the
download, and in the import window when you send a 3.0 order from the web vault to this AutoCAD.

**Placed where it belongs.** In a drawing with no geographic location, the order goes in at its own
coordinates and a notice says so. A drawing whose geographic location, or in Civil 3D whose coordinate
zone, is another coordinate system is refused by name, and nothing is placed. The plugin never sets the
location, the zone or the drawing's units.

**Civil 3D.** The surface is built only where the drawing's Civil 3D drawing units are the order's; in
any other drawing the import names both units and places the rest. In plain AutoCAD the import says the
surface needs Civil 3D and places the rest.

**One ribbon tab.** A Mantle Place tab holds Sign In, Sign Out, Vault, Import Bundle, About Mantle Place
and Send Feedback. Each is also a command you can type.

**Your vault, in AutoCAD.** Sign in once in your browser. The Vault window lists and searches the bundles
you keep, prepares and downloads them and imports them into the drawing, and a notice tells you when a new order arrives. While you
are signed in, this AutoCAD is also a destination you can send an order to from the web vault.

**Credit and sharing.** Every file in a bundle states its licence and sources, and the bundle's README
gives the credit line to use.

**Yours to keep.** The bundle is downloaded to you, and the import is ordinary AutoCAD and Civil 3D
objects in your drawing.

**Account and sign-in.** A bundle import needs no account and no sign-in. Signing in, to reach your vault,
happens once in your browser, and AutoCAD never sees your password. Ordering a bundle needs a mantle.place
account.

**Support.** Send Feedback on the ribbon opens the feedback form, https://mantle.place/feedback; it
needs no account.

## What is new in 1.2.0

The version description. This is the plugin's first storefront release, so it lists what the plugin
does rather than what changed:

- Imports a Mantle Place bundle into the active drawing: contours, the aerial image, public-record
  layers such as parcels and zoning, site linework and more, at the order's own coordinates.
- In Civil 3D, builds the terrain as a TIN surface from the order's LandXML.
- Reads MPB 3.0 bundles beside MPB 2.x ones, and offers Max, Med or Min, with each level's size, before
  an MPB 3.0 order downloads.
- Shows every import's steps in a Bundle Import window, and checks every file before anything is drawn.
- A Vault window to list, prepare, download and import the bundles you keep, once signed in.
- Imports a bundle from disk with no account.

## Known issues

- A public-record layer whose drawing holds points or hatches, rather than polylines, is named as not
  placed, with the reason; the other layers still go in.
- When this version downloads an MPB 3.0 order, it does not ask for the habitat public-record layer; a
  bundle that already holds it imports it.
- The import window has no per-layer choice: an import brings in every part the bundle holds.
- The plugin never changes a drawing's units, location or zone: set them under Drawing Settings, and
  import again.

## Support information

Send Feedback on the plugin's Mantle Place tab opens the Mantle Place feedback form in your browser; it
needs no account. The form is also at https://mantle.place/feedback, and email reaches us at
contact@mantle.place. The plugin's help page, which F1 over any of its buttons opens, is
https://github.com/mantleplace/mantleplace-dcc/blob/main/autocad/README.md.

## Technical details

- Compatibility: AutoCAD 2025, 2026 and 2027, and Civil 3D 2025, 2026 and 2027, from one package.
  Civil 3D's part loads only when Civil 3D is running.
- Platform: Windows 64-bit. The plugin installs for your Windows account and needs no administrator
  rights, and no .NET install: AutoCAD brings its own.
- Network: signing in, the vault and a send from the web vault need an internet connection; importing a
  bundle from disk does not.
- Install from the storefront, or copy the release package's `MantlePlaceAutoCad.bundle` folder into
  `%APPDATA%\Autodesk\ApplicationPlugins`. Use one way in, not both.
- Distribution: one Autodesk application plugin bundle, `MantlePlaceAutoCad.bundle`, holding the plugin
  files, an `Install.cmd` and a README.
