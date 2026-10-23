# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""Hold the validator to the shared conformance corpus's `bundle` group.

A `bundle` case is a JSON description of a synthetic bundle, never a bundle itself: `files` maps
each archive path to its content, a string written as UTF-8 or a JSON value written as JSON. The
runner builds each case three ways, as a folder, a zip and a zip with one wrapper folder, and
requires the same verdict from all three.

The index entry decides what the validator must report:

- `expect: accept` — the verdict is pass; `expect: reject` — the verdict is fail.
- `expectations.errors` / `expectations.warnings` — the finding codes reported at that
  severity, exactly: a reject case fails for its stated reason and no other, and a case that
  lists no warnings must produce none.
- `expectations.hashesVerified` — the number of files whose sha256 was verified.

An expectation key the runner does not know fails the case: a runner that reads past an
assertion it does not understand would pass a corpus full of assertions it never made.
"""

from __future__ import annotations

import json
import tempfile
import zipfile
from pathlib import Path

from .archive import unsafe
from .schemas import SchemaSource
from .validate import validate

GROUP = "bundle"
FORMS = ("folder", "zip", "wrapped")
_EXPECTATION_KEYS = frozenset({"errors", "warnings", "hashesVerified"})
_WRAPPER = "Bundle/"


class CorpusError(Exception):
    """The corpus itself is unreadable; nothing was run."""


def _materialise(files: object) -> dict[str, bytes]:
    if not isinstance(files, dict) or not files:
        raise ValueError("the case has no `files` object")
    out: dict[str, bytes] = {}
    for name, content in files.items():
        if unsafe(name):
            raise ValueError(f"the case names an unsafe path {name!r}")
        if isinstance(content, str):
            out[name] = content.encode("utf-8")
        else:
            out[name] = (json.dumps(content, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    return out


def _build(form: str, files: dict[str, bytes], where: Path) -> Path:
    if form == "folder":
        for name, data in files.items():
            target = where / "bundle" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        return where / "bundle"
    prefix = _WRAPPER if form == "wrapped" else ""
    path = where / f"{form}.zip"
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in files.items():
            archive.writestr(prefix + name, data)
    return path


def _judge(case: dict, report) -> list[str]:
    problems: list[str] = []
    want = {"accept": "pass", "reject": "fail"}.get(case.get("expect"))
    if want is None:
        return [f"expect={case.get('expect')!r} is not accept or reject"]
    if report.verdict != want:
        codes = sorted({f.code for f in report.findings if f.severity == "error"})
        problems.append(f"verdict {report.verdict}, expected {want} (errors: {', '.join(codes) or 'none'})")
    expectations = case.get("expectations") or {}
    unknown = sorted(k for k in expectations if k not in _EXPECTATION_KEYS and not k.startswith("$"))
    if unknown:
        problems.append(f"unknown expectation key(s) {', '.join(unknown)}")
    for severity, key in (("error", "errors"), ("warning", "warnings")):
        reported = sorted({f.code for f in report.findings if f.severity == severity})
        expected = sorted(set(expectations.get(key, [])))
        if reported != expected:
            problems.append(f"{key}: reported {reported or 'none'}, expected {expected or 'none'}")
    if "hashesVerified" in expectations and report.hashes["verified"] != expectations["hashesVerified"]:
        problems.append(f"verified {report.hashes['verified']} hash(es), expected "
                        f"{expectations['hashesVerified']}")
    return problems


def run_corpus(corpus: Path, schemas: SchemaSource) -> tuple[int, list[str]]:
    """(cases run, failure lines). Raises CorpusError when the corpus cannot be read."""
    try:
        index = json.loads((corpus / "index.json").read_text(encoding="utf-8"))
        cases = [c for c in index["cases"] if isinstance(c, dict) and c.get("group") == GROUP]
    except (OSError, ValueError, KeyError, TypeError) as exc:
        raise CorpusError(f"{corpus / 'index.json'} is not a readable corpus index: {exc}") from exc
    if not cases:
        return 0, [f"the corpus has no `{GROUP}` cases; a run that asserts nothing is not a pass"]
    failures: list[str] = []
    for case in cases:
        case_id = case.get("id", "?")
        try:
            body = json.loads((corpus / case["file"]).read_text(encoding="utf-8"))
            files = _materialise(body.get("files") if isinstance(body, dict) else None)
        except (OSError, ValueError, KeyError, TypeError) as exc:
            failures.append(f"{case_id}: the case cannot be read: {exc}")
            continue
        for form in FORMS:
            with tempfile.TemporaryDirectory(prefix="mpb-corpus-") as tmp:
                report = validate(_build(form, files, Path(tmp)), schemas=schemas)
                if report.incomplete:
                    raise CorpusError(f"{case_id} ({form}) could not be checked: "
                                      + "; ".join(f.message for f in report.findings
                                                  if f.severity == "error"))
                failures.extend(f"{case_id} ({form}): {p}" for p in _judge(case, report))
    return len(cases), failures
