"""Report when the published bundle-manifest schema is ahead of `verified-against.json`.

The scheduled drift workflow runs this. It prints nothing when every host's pin is level with the
newest published schema (or only a patch behind, which `spec/compatibility.md` section 2 makes
editorial). When some host is behind, it prints one JSON object: the `title` and `body` of the single
tracking issue, the `newest` version and the `hosts` behind. The workflow finds the open issue that
carries `MARKER` and edits it, or opens one if none exists, so drift surfaces once.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import check_manifest_conformance as gate  # noqa: E402

#: Hidden marker the workflow searches for to find the one tracking issue.
MARKER = "<!-- manifest-drift-tracker -->"


def build_report(pinned_doc: dict) -> dict | None:
    """The tracking issue's content, or None when no host is behind by a minor or major."""
    behind: list[str] = []
    newest_overall: str | None = None
    for host, entry in gate.host_entries(pinned_doc):
        pinned = gate.version_key(entry.get("verifiedAgainstManifestVersion"))
        if pinned is None:
            continue
        newest = gate.newest_published(pinned)
        if gate.sort_key(newest) <= gate.sort_key(pinned) or gate.is_patch_over(pinned, newest):
            continue
        behind.append(host)
        if newest_overall is None or gate.sort_key(newest) > gate.sort_key(newest_overall):
            newest_overall = newest
    if newest_overall is None:
        return None
    label = gate.describe(newest_overall)
    body = (
        f"{MARKER}\n"
        f"The platform publishes manifest {label}, ahead of `tools/manifest-conformance/verified-against.json` "
        f"for: {', '.join(behind)}.\n\n"
        f"To close this: confirm each host's parser handles the {label} shape (add a corpus case), raise "
        f"`verifiedAgainstManifestVersion` in that host's entry and refresh its `evidence`. "
        f"Run `python tools/manifest-conformance/check_manifest_conformance.py --mode live` to see the detail.\n\n"
        f"This issue is kept by the scheduled drift job: it updates this one rather than opening another."
    )
    return {
        "newest": newest_overall,
        "hosts": sorted(behind),
        "title": f"Verify the hosts against the published manifest schema {label}",
        "body": body,
    }


def render(pinned_doc: dict) -> str:
    report = build_report(pinned_doc)
    return "" if report is None else json.dumps(report)


def main() -> int:
    doc = json.loads(gate._PINNED.read_text(encoding="utf-8"))
    out = render(doc)
    if out:
        print(out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
