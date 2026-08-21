<!-- Thanks for the patch. CONTRIBUTING.md is short and names the things that get a pull request
     closed unread — the checklist below is that document compressed. -->

## What this changes, and why



## Checklist

- [ ] **Every commit carries a DCO sign-off** (`git commit -s`) — see
      [CONTRIBUTING.md](https://github.com/mantleplace/mantleplace-dcc/blob/main/CONTRIBUTING.md#sign-your-commits-off-dco).
- [ ] **The conformance suite passes locally**
      (`python -m unittest discover -s tools/manifest-conformance`), and any behaviour it pins that
      this change alters comes with a changed corpus case, with a `reason`.
- [ ] **No locally derived placement values.** The plugins apply what the manifest publishes; a
      patch that computes a survey point, an EPSG zone or a landscape scale locally is declined on
      principle, even when the arithmetic is right.
- [ ] **No new binaries, no sample bundles, no auth/secret-store patches** (the last are
      report-don't-patch — [SECURITY.md](https://github.com/mantleplace/mantleplace-dcc/blob/main/SECURITY.md)).

## If this touches `unreal/`

The Unreal compile is not in public CI — it runs privately, after merge. If you built the plugin
against a local UE 5.8 install, say so here; it shortens the review loop more than anything else
you can do. New C++ follows [`unreal/.clang-format`](https://github.com/mantleplace/mantleplace-dcc/blob/main/unreal/.clang-format); existing
files are not reformatted.
