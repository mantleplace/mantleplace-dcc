---
name: adr-0001-per-host-release-tracks
description: Why each DCC host gets its own release track, tagged `<host>-<version>` with no `v`, and why the first three bare tags stay exactly as published. Read when tagging a release, naming a release asset, or proposing a shared version number or a changelog file.
status: accepted
---

# Per-host release tracks, with prefixed tags and no `v`

This repository ships a plugin per DCC host, but its first three releases (`v0.1.0`–`v0.3.0`) were
Unreal's alone under a bare `vX.Y.Z` tag namespace, and no Revit release had ever been published.
Each host now gets its own release track: an independent version series, an independent cadence, and
a tag of the form `<host>-<version>` — `revit-0.1.0`, `unreal-0.4.0`.

The forcing constraint is that the hosts have non-overlapping release gates. Unreal gates on a
private licensed engine compile; Revit gates on a human launching Revit 2025, 2026 and 2027 and
completing a real import in each, because CI can never build the Revit add-in shim (it needs
`RevitAPI.dll` from a licensed install, which is neither vendored nor redistributable, and this
repository must never carry a self-hosted runner). A single shared version would force every Unreal
patch to either drag someone through the three-Revit smoke test or publish a verification claim
nobody performed. The hosts share a *contract* — the manifest schema and the conformance corpus,
already versioned per host in `tools/manifest-conformance/verified-against.json` — not a codebase.

## Considered options

- **Bare `vX.Y.Z` stays Unreal's forever, prefixes only for other hosts.** Rejected: it costs
  nothing today and permanently encodes "Unreal is the real host" into the tag namespace, which
  contradicts the root rule that a top-level folder *is* a DCC host. The asymmetry compounds with
  every host added.
- **One shared version, per-host assets on each release.** Rejected for the gate reason above.
- **Rewriting the three published tags for uniformity.** Refused: they are published, GitHub reports
  them as immutable, and anyone who has cloned already has them.
- **Keeping the `v`.** Dropped. semver.org is explicit that `v1.2.3` is not a semantic version — the
  `v` disambiguates a bare number in a flat namespace, a job the host prefix already does. Nothing
  here parses tags (there is no release workflow), and no artifact in the repository carries a `v`:
  the `.uplugin` declares `"0.3.0"` and `revit/Directory.Build.props` declares `0.1.0`. Without it,
  a tag is the host plus the exact string the artifact declares, so tag-matches-artifact is a string
  equality.

## Consequences

- `v0.1.0`–`v0.3.0` stay exactly as published, unrenamed, as Unreal's pre-history. The scheme has a
  visible one-time discontinuity at `unreal-0.4.0`; that release's body explains it in a sentence.
- GitHub's "Latest" is a single per-repo pointer and cannot be per-track. It is left to resolve to
  whichever track published most recently; release titles carry the host, which is what actually
  disambiguates. Pinning it to Unreal would make a fresh Revit release invisible, which is the
  failure this decision exists to end.
- There is no changelog file. Each release body is its track's changelog and provenance record, and
  links back to the previous release of the same track — GitHub offers no per-prefix filter, so that
  back-link is the only per-track history view.
- Asset names drop the `v` too: `MantlePlace-UE5.8-Win64-0.4.0.zip`.
