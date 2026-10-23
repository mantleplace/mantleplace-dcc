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


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_schema_dir(root: Path, schemas: dict[str, dict] | None = None,
                     ledger: dict[str, str] | None = None) -> Path:
    """A schema directory as `--schema-dir` reads it: `<key>.json` plus `frozen.lock.json`."""
    root.mkdir(parents=True, exist_ok=True)
    schemas = schemas if schemas is not None else {"1.14.0": SYNTHETIC_SCHEMA}
    frozen = {}
    for key, schema in schemas.items():
        data = json.dumps(schema, indent=2).encode("utf-8")
        (root / f"{key}.json").write_bytes(data)
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
