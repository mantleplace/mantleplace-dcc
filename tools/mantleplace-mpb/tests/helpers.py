# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""Synthetic bundles and synthetic schemas for the suite. Nothing here is a copy of the published
schema: the published series is fetched at run time, and these tests pin the validator's own
behaviour against small schemas written for the purpose."""

from __future__ import annotations

import hashlib
import json
import sys
import zipfile
from pathlib import Path

# Run from a checkout without installing: the package source sits beside this folder.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

SCHEMA_2020_12 = "https://json-schema.org/draft/2020-12/schema"

#: Stands in for a published schema: it requires what every published one does at the top.
SYNTHETIC_SCHEMA = {
    "$schema": SCHEMA_2020_12,
    "type": "object",
    "required": ["version", "layout"],
    "properties": {
        "version": {"const": "1.14.0"},
        "layout": {"type": "object", "required": ["readme", "manifest"]},
    },
}


#: The same, for the 2.0 major: the version it pins is the only difference.
SYNTHETIC_SCHEMA_2 = {**SYNTHETIC_SCHEMA, "properties": {**SYNTHETIC_SCHEMA["properties"],
                                                        "version": {"const": "2.0.0"}}}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_schema_dir(root: Path, schemas: dict[str, dict] | None = None,
                     ledger: dict[str, str] | None = None, staged: tuple[str, ...] = ()) -> Path:
    """A schema directory as `--schema-dir` reads it: `<key>.json` plus `frozen.lock.json`.

    A key in `staged` is served (its `<key>.json` is written) and left out of the ledger.
    """
    root.mkdir(parents=True, exist_ok=True)
    schemas = schemas if schemas is not None else {"1.14.0": SYNTHETIC_SCHEMA}
    frozen = {}
    for key, schema in schemas.items():
        data = json.dumps(schema, indent=2).encode("utf-8")
        (root / f"{key}.json").write_bytes(data)
        if key not in staged:
            frozen[key] = sha256(data)
    if ledger is not None:
        frozen = ledger
    (root / "frozen.lock.json").write_text(json.dumps({"frozen": frozen}), encoding="utf-8")
    return root


def manifest(**extra: object) -> dict:
    document: dict = {
        "version": "1.14.0",
        "layout": {"readme": "README.md", "manifest": "Metadata/manifest.json"},
    }
    document.update(extra)
    return document


def bundle_files(document: dict | None = None) -> dict[str, bytes]:
    """The two files every bundle has: its README and its manifest."""
    return {
        "README.md": b"A synthetic bundle.\n",
        "Metadata/manifest.json": json.dumps(document or manifest(), indent=2).encode("utf-8"),
    }


def make_folder(root: Path, files: dict[str, bytes]) -> Path:
    for name, data in files.items():
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    return root


def make_zip(path: Path, files: dict[str, bytes], prefix: str = "") -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w") as archive:
        for name, data in files.items():
            archive.writestr(prefix + name, data)
    return path


def write_corpus_index(root: Path, versions: list[object], rejects: list[object] = ()) -> Path:
    """A corpus `index.json` with one accept case per entry of `versions` (None: a case declaring
    none), and one reject case per entry of `rejects`."""
    cases = []
    for number, (version, expect) in enumerate(
            [(v, "accept") for v in versions] + [(v, "reject") for v in rejects]):
        case: dict = {"id": f"case.{number}", "group": "manifest", "file": "x.json", "expect": expect}
        if version is not None:
            case["manifestVersion"] = version
        cases.append(case)
    index = root / "index.json"
    index.write_text(json.dumps({"corpusVersion": 3, "manifestVersion": "1.0.0", "cases": cases}),
                     encoding="utf-8")
    return index


def geopackage(*tables: str) -> bytes:
    """A GeoPackage that holds one feature table per name, as the validator reads one."""
    from mantleplace_mpb.corpus import geopackage_bytes

    return geopackage_bytes(list(tables))
