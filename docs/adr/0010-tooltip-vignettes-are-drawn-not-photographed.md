---
name: adr-0010-tooltip-vignettes-are-drawn-not-photographed
description: Revit's extended-tooltip pictures are drawn from committed text - composed Tabler icons and a computed isometric toposolid - rather than screenshotted from a running Revit, and they ship at 355x266 because Revit caps a tooltip image at 355 px and enforces it silently. Read before adding a screenshot, before rendering one larger, and before putting a vignette on a third button.
status: accepted
---

# 10. Tooltip vignettes are drawn, not photographed

Date: 2026-09-17

## Status

Accepted.

## Context

Native Revit buttons show a picture under the long description on hover. Hovering `Toposolid` shows
one, and that picture is a large part of why an Autodesk button feels explained. The Mantle Place
ribbon showed text alone.

The obvious way to match it is a screenshot: photograph the toposolid a real import produces, crop
it, commit it. It is also what the originating issue asked for, in as many words — *"a small
screenshot or diagram"* — and a future reader finding this record should know that the issue and
the repository disagree on purpose.

A screenshot is the more persuasive picture. It is instantly recognisable, it is unarguably true,
and it shows detail no line drawing can. It is also:

- **Not reproducible from anything committed.** Every other image in this repository either renders
  from a committed source or renders from a private one whose script is here
  ([ADR 0009](0009-host-assets-render-the-monogram.md)). A photograph has neither.
- **Permanent.** A drawing can be replaced by a photograph freely. A photograph committed once is
  in the history forever, and the only way back is a force-push.
- **Bound to one of everything** — one Revit version's chrome, one UI theme, one display scale, one
  project's content, one moment in the plugin's own look. Revit 2025 and 2027 do not look alike.
- **Unregenerable by anyone but a person with a licensed Revit.** No runner, no contributor without
  Revit, and no agent can refresh it when the thing it photographs changes.

Two constraints then shaped what a drawing could be.

**Revit caps a tooltip image at 355 pixels on its longest side.** The API says so in 2025, 2026 and
2027 alike, and it enforces the cap *silently*: an over-large image is clipped or dropped with no
error, on a surface that only appears after a hover delay. There is no room under that cap for the
usual trick of rendering at twice the layout size for high-DPI crispness — a 2x render of a 355 px
picture is 710 px, twice the limit.

**The API also settles which buttons are eligible.** *"SplitButton and RadioButtonGroup cannot
display the tooltip set by this method."* The Account button is a split button, so it was never a
candidate whatever its content might have been.

## Decision

**Revit's tooltip vignettes are drawn from committed text, and rendered by a script in this
repository.**

- **Two buttons, `Vault` and `Import Bundle`.** A content rule, not a budget one: the other three
  would be a picture of a window opening, of a browser, or — for `Probe Terrain` — of a model that
  by design did not change. A picture on every button is how the affordance stops meaning anything.
- **Two methods, one pen.** `Vault` is composed from unedited Tabler icons; `Import Bundle` is a
  computed isometric toposolid, an analytic height field sampled on a grid and projected. Both are
  drawn with Tabler's 2-on-24 stroke ratio at a 96 px element box, round caps and joins, the two
  theme greys and the one brand orange. Method may differ; idiom may not, or the two pictures read
  as coming from two products.
- **355 x 266, at 96 DPI, one file per theme, no size in the file name.** The largest 4:3 under the
  cap. The absence of a size suffix is deliberate: every other render in that folder carries one
  because a ladder exists to choose from, and a name shaped like those would invite a size that
  nothing reads.
- **The cap is asserted, not merely obeyed.** `Vignettes.MaxPixels` carries the number and the
  headless suite reads each committed file's PNG header against it. That assertion is the whole
  agreement between the render script and the plugin — the script carries its own literals, and a
  render regenerated at the wrong size fails the build rather than going missing in Revit.
- **`Import Bundle` draws the toposolid and nothing else.** An import can also produce site
  boundaries, road centrelines, vegetation and an imagery drape, every one of them conditional on
  what the bundle carries. A picture with trees in it promises trees.

## Consequences

**A high-DPI display gets a softened picture.** 355 px is the whole budget, so a 200% display
upscales. That is the cap's consequence, not a choice, and the alternative under it is no picture.

**The pictures are less informative than a photograph would be.** This is the cost, and it is real.
A line drawing of a toposolid is a claim about what arrives; a screenshot would be evidence of it.
What is bought instead is that the picture regenerates, ages with nothing, and can be corrected by
anyone with a text editor and PowerShell.

**Adding a vignette to a third button is a decision, not a chore.** It needs an answer to *what does
this produce that a picture can show*, and `Probe Terrain` and `Open Logs` do not have one.

**A new kind of host imagery now exists** — not a glyph, which says what a command does, and not the
mark, which says whose it is. The word for it is fixed in [CONTEXT.md](../../CONTEXT.md), which owns
what it means; this record deliberately does not restate the definition or count the family, because
that is the drift the one-home rule exists to stop.

**The cap cannot be checked by looking at the ribbon.** Nothing fails, nothing logs; the picture is
simply absent. The PNG-header assertion is the only detector there can be, which is why it exists
rather than being left to the render script's own arithmetic.

**Verification is still eyes on a running Revit.** Whether the tooltip lays out, whether the picture
clips against a fifty-word long description, and whether a live theme change repaints it are
questions no headless suite can reach. They are checked the way the Account split button and the
ribbon's glyphs were: by opening Revit and hovering.
