# Trademark policy

**The code is forkable. The name is not.**

The [Apache 2.0 licence](LICENSE) that covers this repository's code (the prose in [`spec/`](spec/) is
CC BY 4.0) grants you broad rights over the
*code*. It grants no rights over the **Mantle Place** name, the `mantle.place` domain, or the Mantle
Place logo and marks — including the square "mp" mark that ships in
[`unreal/MantlePlace/Resources/`](unreal/MantlePlace/Resources/) and
[`revit/src/MantlePlace.Revit.Addin/Resources/`](revit/src/MantlePlace.Revit.Addin/Resources/).
Trademark law and copyright law are separate, and Apache 2.0 §6 says so explicitly.

This document says what we will and will not object to, so you do not have to guess.

## The marks

For the avoidance of doubt, the marks this policy covers are:

- the **"Mantle Place"** word mark;
- the **`mantle.place`** domain;
- the square **"mp" mark** and the Mantle Place logo lockups shipped in
  [`unreal/MantlePlace/Resources/`](unreal/MantlePlace/Resources/) and
  [`revit/src/MantlePlace.Revit.Addin/Resources/`](revit/src/MantlePlace.Revit.Addin/Resources/);
- the **"mp" roundel**, the round treatment the Unreal plugin shipped through `unreal-0.4.0`. It
  is no longer rendered anywhere in this repository, and it remains covered: it is installed on
  machines we do not control, so a fork still has to replace it;
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
- the icons and logo in `unreal/MantlePlace/Resources/`;
- the ribbon icons in `revit/src/MantlePlace.Revit.Addin/Resources/`.

You may still say your fork *derives from* Mantle Place. Say it in prose, not in the product name.

## What we will object to

- A modified distribution using the Mantle Place name or marks as its own identity.
- Any use implying endorsement, affiliation or an official relationship that does not exist.
- Domain names, org names, app-store listings or social accounts that a reasonable person would read
  as being us.

## The "MPB 2.x-conformant" statement

The format is open, and so is the right to say you follow it. Anyone may state that their work is
**"MPB 2.x-conformant"**, with no licence, fee, registration or approval from us. The statement is
**self-declared**: we do not certify, audit or endorse it, and making it implies no affiliation.

It is made per class, and each class has its own claim:

- **Producer**: software that writes Mantle Place Bundles may say "MPB 2.x-conformant producer" when
  its manifests validate against the published schema version, which it names.
- **Reader**: software that reads bundles may say "MPB 2.x-conformant reader" when it passes the
  conformance corpus groups that [`spec/conformance.md`](spec/conformance.md) names for a reader.

Name what you tested against. A reader states the corpus pin it passed (the manifest version the corpus
pins, see [`spec/conformance.md`](spec/conformance.md)) and the repo commit or tag it ran, for example
`MPB 2.x-conformant reader (corpus pin <manifest version>, commit <sha>)`. A producer states the schema
version its manifests validate against. A claim that names neither is not a claim we can read. If you
stop passing, stop making the claim.

This clause covers only that phrase. It does not license the **Mantle Place** name, the logo or any
other mark listed above, which stay reserved exactly as written; use the phrase as a description of
your work, not as a product name.

## Fonts and third-party marks

The fonts under `unreal/MantlePlace/Source/ThirdParty/Fonts/` are third-party works under the SIL Open Font
Licence; their own licence texts travel beside them and govern their use. Autodesk, Revit, Epic
Games, Unreal Engine and Cesium are the marks of their respective owners and are used here only to
say, truthfully, what this software runs in.

## Asking

If a use is not clearly covered above, ask before shipping: **support@mantle.place**. We would rather
answer a question than send a notice.
