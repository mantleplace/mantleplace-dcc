---
name: autocad-store-review-1-2-0
description: Draft instructions for the Autodesk storefront's reviewer of Mantle Place for AutoCAD 1.2.0 - a no-account walk-through from the public samples page, then each command on the Mantle Place tab in order with what to expect, in AutoCAD and in Civil 3D. Read before submitting autocad-1.2.0 to the storefront; the founder approves every word.
---

# Reviewer instructions, AutoCAD 1.2.0

**Draft for the founder's approval.** Paste into the storefront's field for reviewer notes, or attach it.
Everything here works without an account: the sample comes from a public page and is imported from
disk. The listing text is in [`store-listing-1.2.0.md`](store-listing-1.2.0.md); what each command does
is the plugin's README ([`../README.md`](../README.md)).

## Before you start

1. Install the plugin from the storefront (or copy the release package's `MantlePlaceAutoCad.bundle`
   folder into `%APPDATA%\Autodesk\ApplicationPlugins`; use one way in, not both).
2. Open `https://mantle.place/mpb-samples` (it opens the Samples page) and download the **Austin
   Downtown** bundle with *Download the bundle (.zip)*. No account or email is asked for. **Do not unzip
   it.** Austin is in metres (UTM 14N); the other three samples import the same way.

   > **Founder note, delete before submitting.** The samples on that page were built before the
   > manifest carried an AutoCAD block, and Import Bundle refuses such a bundle. Submit only once the
   > samples are rebuilt on the current MPB version, then re-run step 4 on the rebuilt sample and check
   > the sample name, its units and the parts step 4 lists against what it shows.
3. Start AutoCAD 2025, 2026 or 2027 and start a new drawing. If AutoCAD asks whether to load Mantle
   Place, choose **Always Load**. A **Mantle Place** tab appears on the ribbon, holding one **Mantle
   Place** panel with six buttons: Sign In, Sign Out, Vault, Import Bundle, About Mantle Place and Send
   Feedback.

## The commands, in order

The buttons are walked left to right. Each one is also a command you can type, named under it.

**1. Sign In** (`MANTLEPLACESIGNIN`). Your browser opens on the Mantle Place sign-in page and a small
window waits for it; AutoCAD stays usable. Signing in needs an account, and the review does not require
it: close the browser tab and the small window.

**2. Sign Out** (`MANTLEPLACESIGNOUT`). The command line reads *Signed out. The stored session on this
machine has been cleared.* Nothing in the drawing changes.

**3. Vault** (`MANTLEPLACEVAULT`). The Vault window opens. Signed out, it cannot list your bundles and
says to sign in first. Signed in, it lists the bundles in your account (none, for a new account). Close it.

**4. Import Bundle** (`MANTLEPLACEIMPORTBUNDLE`). A file dialog titled *Choose a Mantle Place bundle*
opens. Pick the zip. The **Bundle Import** window opens, and the import begins at once: AutoCAD offers no per-layer choice, so
there is nothing to press first. The window lists **Verify Files**, then each part the bundle places,
each marked as it runs, with the step in flight and an **Elapsed** clock. **Cancel** stops the import
between steps; the steps it never reached read **Not Run**. When it ends, the window says what went in
and what did not, with each reason, and the command line says the same. Close the window.

What you should see in the drawing (type `ZOOM` then `E` to see all of it):

- Contours as polylines on the `MantlePlace-Contours` layer, each at its height.
- The aerial image on the `MantlePlace-Aerial` layer.
- Public-record layers, such as parcels and zoning, each on its own `MantlePlace-Records-<name>` layer.
- Site linework on layers under `MantlePlace-Linework`.
- Notices saying the drawing has no geographic location, so the order is placed at its own coordinates,
  and that the surface needs Civil 3D.

A record layer whose drawing holds points or hatches rather than polylines is named as not placed, with
the reason, and the other layers still go in. A file named after the zip and ending
`.mantleplace-import.log` is written beside it, holding the same words.

**5. About Mantle Place** (`MANTLEPLACEABOUT`). A window names the plugin's version and build, says
whether the Civil 3D part is on (off, in plain AutoCAD) and whether you are signed in, and links the
privacy page.

**6. Send Feedback** (`MANTLEPLACEFEEDBACK`). Your browser opens the Mantle Place feedback form. It needs
no account.

**F1** over any button on the panel opens the plugin's help page.

## Again, in Civil 3D

1. Start Civil 3D 2025, 2026 or 2027 (not its "as AutoCAD" launcher) and start a new drawing from a
   **metric** template, so its Civil 3D drawing units are metres, as the Austin sample's are. Leave its
   coordinate zone unset. The same **Mantle Place** tab and panel appear.
2. **About Mantle Place** now says the Civil 3D part is on.
3. **Import Bundle** with the same zip. Everything listed for AutoCAD goes in, and the Bundle Import
   window has one more step: the **Civil 3D surface**, a TIN surface named `MantlePlace-Surface-` and the
   order's id, on the `MantlePlace-Surface` layer, built from the bundle's LandXML. A notice says the
   drawing has no Civil 3D coordinate zone, so the order is placed at its own coordinates.
4. To see the units check: start a drawing from an **imperial** template (drawing units in feet) and
   import the same zip. The surface is skipped with a reason naming both units and where to set them
   (Drawing Settings, Units and Zone), and the rest goes in. The plugin never changes a drawing's units,
   location or zone.

The other commands behave as in AutoCAD.

## What needs an account

Signing in, the Vault's list, and sending an order from the web vault to this AutoCAD need a
mantle.place account; ordering a bundle does too. The review does not require any of them. The level
control (Max, Med and Min, each with its download size) belongs to an MPB 3.0 order in the Vault, or to
one sent from the web vault, so it needs an order of your own to see; the samples page serves MPB 2.0
bundles, which import through the steps above.

## The sample bundles

Each sample carries OpenStreetMap and Overture data under the ODbL; every such file holds its notice,
and the bundle's `README.md` gives the credit line. Keep the files together if you pass them on.
