# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""`validate()`: one bundle in, one report out."""

from __future__ import annotations

import json
from pathlib import Path

from .archive import MANIFEST_PATH, MANIFEST_PATH_3, open_bundle
from .checks import check_frames, check_hashes, check_pointers, check_roles
from .geopackage import check_geopackage
from .layout3 import (CATALOG, check_catalog, check_host_block, check_level, check_tree,
                      major)
from .names import check_names, is_layout_2
from .report import Report
from .schema_subset import UnsupportedSchema, errors
from .schemas import SchemaProblem, SchemaSource, version_key
from .zipmethods import check_zip

#: Past this many, schema errors are summarised: one broken block can fan out into hundreds.
MAX_SCHEMA_FINDINGS = 100

#: A major whose text is written (`spec/format.md`) and whose schema is not published yet. A
#: manifest that declares one, while no schema is served for its version, is held to the text's
#: rules alone and the report says so (`schema.pending`, at info). Once the schema is served the
#: manifest is held to it like any other, so this set changes nothing for a published version.
PENDING_MAJORS = frozenset({3})


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
        where = bundle.manifest_path
        try:
            manifest = json.loads(bundle.read(where).decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as exc:
            report.add("manifest", "manifest.not-json", "error",
                       f"{where} is not UTF-8 JSON: {exc}", file=where)
            return report
        if not isinstance(manifest, dict):
            report.add("manifest", "manifest.not-object", "error",
                       f"{where} is JSON but not an object", file=where)
            return report
        _check_schema(manifest, schemas, report)
        declared = major(manifest.get("version"))
        # `Manifest.json` keeps its name and place in every major after 2 (format.md §2).
        _check_place(where, (declared or 0) >= 3, report)
        if declared == 3:
            # The 3.x tree: one catalog, one level, no per-file frames and no layout pointers.
            check_tree(bundle, report, manifest.get("version"))
            check_level(manifest, report)
            check_host_block(manifest, report)
            check_catalog(manifest, bundle, report)
            check_zip(bundle, report)
            check_roles(manifest, bundle, report, unstated=False, member=CATALOG)
            return report
        if (declared or 0) > 3:
            return report  # A later major: its version is all this validator reads (§14).
        # The 2.0 layout's rules hold a bundle that declares 2.x, and only one: a 1.x bundle is
        # judged by its own version, whose layout and names were different.
        layout_2 = is_layout_2(manifest.get("version"))
        check_pointers(manifest, bundle, report, fold_case=layout_2)
        check_hashes(manifest, bundle, report, fold_case=layout_2)
        check_frames(manifest, report)
        check_roles(manifest, bundle, report, fold_case=layout_2)
        if layout_2:
            check_geopackage(manifest, bundle, report)
            check_names(bundle, report)
    return report


def _check_place(where: str, at_root: bool, report: Report) -> None:
    """A 3.x manifest sits at the root as `Manifest.json`; an earlier one in `Metadata/`."""
    expected = MANIFEST_PATH_3 if at_root else MANIFEST_PATH
    if where != expected:
        report.add("manifest", "manifest.place", "error",
                   f"the manifest was found at {where}, but the version it declares puts it at "
                   f"{expected} (format.md §11), so a reader finds it in the wrong place",
                   file=where)


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
        elif not _pending(key, problem, schemas, report):
            report.add("schema", problem.code, "error", str(problem), pointer="/version")
        return
    if schemas.is_staged(key):
        report.add("schema", "schema.staged", "info",
                   f"version {key} is staged: served at {report.schema_url} but not yet in the "
                   "freeze ledger, so there is no recorded sha256 to hold its bytes to; the "
                   "manifest is held to the served document", pointer="/version")
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


def _pending(key: str, problem: SchemaProblem, schemas: SchemaSource, report: Report) -> bool:
    """Record `schema.pending` when `key` is of a pending major and no schema is served for it."""
    if problem.code != "schema.version-unpublished" or major(key) not in PENDING_MAJORS:
        return False
    try:
        served = schemas.served(key)
    except SchemaProblem as unreachable:
        report.cannot_check("schema", unreachable.code, str(unreachable))
        return True
    if served:
        return False
    report.add("schema", "schema.pending", "info",
               f"no schema is published for {key} yet: the {major(key)}.x schema is not served, "
               "so the manifest is held to the format text's rules alone, and to the schema once "
               "it is published", pointer="/version")
    return True
