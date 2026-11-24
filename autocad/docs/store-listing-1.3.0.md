---
name: autocad-store-listing-1-3-0
description: Draft Autodesk storefront listing text for Mantle Place for AutoCAD 1.3.0, covering AutoCAD and Civil 3D - title, short and long description, what is new, known issues, support and technical details. Read before editing the storefront listing or submitting autocad-1.3.0; it describes only what 1.3.0 does, and the founder approves every word.
---

# Storefront listing text, AutoCAD 1.3.0

**Draft for the founder's approval.** 1.3.0 is the plugin's second storefront submission, and one listing
covers AutoCAD and Civil 3D: they are one plugin. Each section below is the text to paste into the
matching storefront field. It describes only what the plugin does at 1.3.0; where the README
([`../README.md`](../README.md)) or the release notes ([`release-notes-1.3.0.md`](release-notes-1.3.0.md))
own a fact, this file words it for a store page and does not become a second home for it. The steps for
the store's reviewer are in [`store-review-1.3.0.md`](store-review-1.3.0.md). The skeleton every host's
listing follows is [`store-listing-principles.md`](../../docs/store-listing-principles.md); this one
keeps its order and names only what AutoCAD imports.

**Field limits held to.** Autodesk's publisher guide for its app store (*Browser based app submission
process overview*, undated) gives the App Description 4000 characters, Known Issues 1000, Support
Information 1000 and Additional Information 2000, with minimal HTML. The submission form, read on
2026-11-21, also limits the app name to 50 characters and the short description to 200, and gives General
Usage Instructions and Installation/Uninstallation 1000 each. The text below fits those limits; where the
form shows a different one, the form wins. Technical details go in Additional Information, and the
**Learn More Url** is `https://mantle.place/autocad`. The same guide says "AutoCAD" in the compatibility
list means plain AutoCAD only, so Civil 3D is ticked as a product of its own.

No host logo appears in the listing or in any image submitted with it. The listing's icon is the round
Mantle Place mark, the same one the Revit listing uses, the company logo
([ADR 0033](../../docs/adr/0033-every-host-shows-the-round-mark-with-its-extrude.md)); the plugin's buttons
and window title bars carry the same mark and no other logo.

**Price** is Free, and follows the storefront's answer on the pricing category: the founder has asked the
storefront to keep the listing Free on the strength of the description's paragraph on what is free and what
is bought, and sets Trial if the storefront still requires it, with no change to the text.

## App name

Mantle Place — Real-World Sites

(The form allows 50 characters, read on 2026-11-21, so the name is the Revit listing's own.)

## Short description

Bring a real site into a drawing: contours, aerial imagery, parcels, zoning, site linework and, in Civil
3D, a TIN surface, from a bundle you ordered or downloaded. Importing needs no account.

(192 of the form's 200 characters.)

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

**Free, with nothing locked.** The plugin is free, with no time limit and no locked commands. Site bundles
are ordered on mantle.place, where each site's price is shown before you buy. Free sample bundles are at
mantle.place/samples.

**Account and sign-in.** A bundle import needs no account and no sign-in. Signing in, to reach your vault,
happens once in your browser, and AutoCAD never sees your password. Ordering a bundle needs a mantle.place
account.

**Support.** Send Feedback on the ribbon opens the feedback form, https://mantle.place/feedback; it
needs no account.

## What is new in 1.3.0

The version description:

- The import window lists each step's time in its closing report, under Step times (seconds).
- The vault and import windows list only what an order includes, with no row for an Extra the order does
  not include and no wording about paid sites.
- The round Mantle Place mark is on the ribbon buttons, the window title bars and the bundle's icon.
- Sign In refuses a configuration that splits it across two servers, naming both, and names a server that
  does not answer.

## Known issues

- A public-record layer whose drawing holds points or hatches, rather than polylines, is named as not
  placed, with the reason; the other layers still go in.
- When this version downloads an MPB 3.0 order, it does not ask for the habitat public-record layer; a
  bundle that already holds it imports it.
- The import window has no per-layer choice: an import brings in every part the bundle holds.
- The plugin never changes a drawing's units, location or zone: set them under Drawing Settings, and
  import again.

## General usage instructions

1. Get a bundle. Order a site on mantle.place, or download a free sample bundle from mantle.place/samples
   (no account needed).
2. Import it. Start a new drawing, then Mantle Place > Import Bundle and pick the zip, or sign in and use
   Vault to download an order and import it.
3. Watch it run. The Bundle Import window marks each step as it runs and can cancel between steps; when it
   ends it says what went in and what did not, and why. Type ZOOM then E to see all of it.
4. In Civil 3D, start from a template in the order's units (metres or feet), and the terrain is also
   built as a TIN surface.

Press F1 over any Mantle Place button for help.

## Installation and uninstallation

Requirements: AutoCAD 2025, 2026 or 2027, or Civil 3D 2025, 2026 or 2027, on Windows 64-bit; one package
serves all of them. Importing a bundle from disk needs no account; the vault needs a free Mantle Place
account.

Install: close AutoCAD, install from this store, and start AutoCAD. If AutoCAD asks whether to load Mantle
Place, choose Always Load. A Mantle Place tab appears on the ribbon. If you copied Mantle Place in from the
zip on GitHub before, remove that `MantlePlaceAutoCad.bundle` folder first, so the plugin does not load
twice.

Uninstall: close AutoCAD and uninstall Mantle Place from Windows Settings > Apps > Installed apps. Your
drawings keep everything an import made.

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
