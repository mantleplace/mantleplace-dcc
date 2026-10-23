# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""`validate()`: one bundle in, one report out."""

from __future__ import annotations

import json
from pathlib import Path

from .archive import MANIFEST_PATH, open_bundle
from .checks import check_frames, check_hashes, check_pointers, check_roles
from .report import Report
from .schema_subset import UnsupportedSchema, errors
from .schemas import SchemaProblem, SchemaSource, version_key

#: Past this many, schema errors are summarised: one broken block can fan out into hundreds.
MAX_SCHEMA_FINDINGS = 100


def validate(path: str | Path, *, schemas: SchemaSource | None = None,
             strict: bool = False) -> Report:
    """Validate the bundle at `path` (a zip, a folder, or either inside one wrapper folder)."""
    report = Report(input=str(path), strict=strict)
    schemas = schemas if schemas is not None else SchemaSource()
    bundle = open_bundle(Path(path), report)
    if bundle is None:
        return report
    with bundle:
        report.ran.append("manifest")
        try:
            manifest = json.loads(bundle.read(MANIFEST_PATH).decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as exc:
            report.add("manifest", "manifest.not-json", "error",
                       f"{MANIFEST_PATH} is not UTF-8 JSON: {exc}", file=MANIFEST_PATH)
            return report
        if not isinstance(manifest, dict):
            report.add("manifest", "manifest.not-object", "error",
                       f"{MANIFEST_PATH} is JSON but not an object", file=MANIFEST_PATH)
            return report
        _check_schema(manifest, schemas, report)
        check_pointers(manifest, bundle, report)
        check_hashes(manifest, bundle, report)
        check_frames(manifest, report)
        check_roles(manifest, bundle, report)
    return report


def _check_schema(manifest: dict, schemas: SchemaSource, report: Report) -> None:
    report.ran.append("schema")
    if "version" not in manifest:
        report.add("schema", "schema.version-missing", "error",
                   "the manifest declares no version, so there is no schema to hold it to",
                   pointer="/version")
        return
    declared = manifest["version"]
    report.manifest_version = declared
    key = version_key(declared)
    if key is None:
        report.add("schema", "schema.version-invalid", "error",
                   f"version {declared!r} is neither an integer (the pre-history) nor a semver "
                   "string such as \"1.14.0\"", pointer="/version")
        return
    report.schema_url = schemas.url(key)
    try:
        schema = schemas.load(key)
    except SchemaProblem as problem:
        if problem.cannot_check:
            report.cannot_check("schema", problem.code, str(problem))
        else:
            report.add("schema", problem.code, "error", str(problem), pointer="/version")
        return
    try:
        found = errors(schema, manifest)
    except UnsupportedSchema as exc:
        report.cannot_check("schema", "schema.unsupported",
                            f"the schema for {key} cannot be evaluated here: {exc}")
        return
    for pointer, message in found[:MAX_SCHEMA_FINDINGS]:
        report.add("schema", "schema.invalid", "error", f"{pointer or '/'}: {message}",
                   pointer=pointer or "")
    if len(found) > MAX_SCHEMA_FINDINGS:
        report.add("schema", "schema.invalid", "error",
                   f"and {len(found) - MAX_SCHEMA_FINDINGS} more schema errors")
