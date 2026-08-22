# Trademark policy

**The code is forkable. The name is not.**

The [Apache 2.0 licence](LICENSE) that covers this repository grants you broad rights over the
*code*. It grants no rights over the **Mantle Place** name, the `mantle.place` domain, or the Mantle
Place logo and marks — including the "mp" roundel that ships in
[`unreal/MantlePlace/Resources/`](unreal/MantlePlace/Resources/). Trademark law and copyright law are
separate, and Apache 2.0 §6 says so explicitly.

This document says what we will and will not object to, so you do not have to guess.

## The marks

For the avoidance of doubt, the marks this policy covers are:

- the **"Mantle Place"** word mark;
- the **`mantle.place`** domain;
- the **"mp" roundel** and the Mantle Place logo lockups shipped in
  [`unreal/MantlePlace/Resources/`](unreal/MantlePlace/Resources/);
- the tagline **"place it on mantle"**.

All are trademarks (™) of Mantle Place LLC.

## What you may do

- **Redistribute this software unmodified**, under its own name, including through package
  managers, marketplaces and mirrors. An unmodified build may keep the Mantle Place name and marks —
  that is what the name is *for*: it tells a user what they have.
- **State accurately what your work is.** "A fork of Mantle Place for Revit", "compatible with Mantle
  Place bundles", "reads the Mantle Place bundle manifest" are all fine. Referring to us by name in
  order to say something true about us is nominative use and needs no permission.
- **Use the name in documentation, articles, talks and comparisons.**

## What requires a rename

**If you modify the code and distribute the result, rename it.** Pick your own product name, your own
plugin id, and your own icons. A modified build carrying our name means a user's bug report, security
expectation and support question land on the wrong maintainer — which is the harm this policy exists
to prevent, and the only one.

Concretely, a fork changes:

- the plugin display name and description in `unreal/MantlePlace/MantlePlace.uplugin`;
- the Revit ribbon text and `.addin` manifest name under `revit/src/MantlePlace.Revit.Addin/`;
- the icons and logo in `unreal/MantlePlace/Resources/`.

You may still say your fork *derives from* Mantle Place. Say it in prose, not in the product name.

## What we will object to

- A modified distribution using the Mantle Place name or marks as its own identity.
- Any use implying endorsement, affiliation or an official relationship that does not exist.
- Domain names, org names, app-store listings or social accounts that a reasonable person would read
  as being us.

## Fonts and third-party marks

The fonts under `unreal/MantlePlace/Resources/Fonts/` are third-party works under the SIL Open Font
Licence; their own licence texts travel beside them and govern their use. Autodesk, Revit, Epic
Games, Unreal Engine and Cesium are the marks of their respective owners and are used here only to
say, truthfully, what this software runs in.

## Asking

If a use is not clearly covered above, ask before shipping: **support@mantle.place**. We would rather
answer a question than send a notice.
