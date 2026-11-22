# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import os
import sqlite3
import tempfile

from .archive import Bundle
from .report import Report

_CHECK = "geopackage"

_FEATURE_TABLES = (
    "SELECT DISTINCT c.table_name FROM gpkg_contents c "
    "JOIN gpkg_geometry_columns g ON g.table_name = c.table_name "
    "WHERE c.data_type = 'features' ORDER BY c.table_name"
)


def check_geopackage(manifest: dict, bundle: Bundle, report: Report) -> None:
    report.ran.append(_CHECK)
    twins = _projected_variants(manifest)
    seen: set[str] = set()
    for pointer, path in _untabled_rows(manifest):
        for candidate in (path, twins.get(path)):
            if candidate is None:
                continue
            actual = bundle.resolve(candidate, fold_case=True)
            if actual is None or actual in seen or not bundle.has_file(actual):
                continue
            seen.add(actual)
            _check_file(bundle, actual, pointer, report)


def _projected_variants(manifest: dict) -> dict[str, str]:
    layout = manifest.get("layout")
    twins = layout.get("projected_variants") if isinstance(layout, dict) else None
    if not isinstance(twins, dict):
        return {}
    return {k: v for k, v in twins.items() if isinstance(k, str) and isinstance(v, str)}


def _untabled_rows(manifest: dict) -> list[tuple[str, str]]:
    vector = manifest.get("vector")
    layers = vector.get("layers") if isinstance(vector, dict) else None
    if not isinstance(layers, list):
        return []
    found: list[tuple[str, str]] = []
    for i, layer in enumerate(layers):
        formats = layer.get("formats") if isinstance(layer, dict) else None
        if not isinstance(formats, list):
            continue
        for j, fmt in enumerate(formats):
            if not isinstance(fmt, dict) or fmt.get("format") != "gpkg":
                continue
            path = fmt.get("path")
            if not isinstance(path, str) or not path.strip() or fmt.get("tables"):
                continue
            found.append((f"/vector/layers/{i}/formats/{j}", path.strip()))
    return found


def _check_file(bundle: Bundle, path: str, pointer: str, report: Report) -> None:
    tables = _feature_tables(bundle, path)
    if tables is None:
        report.add(_CHECK, "gpkg.unreadable", "info",
                   f"{path} is not a GeoPackage this check can read, so the number of its feature "
                   "tables is not counted", pointer=pointer, file=path)
    elif len(tables) > 1:
        report.add(_CHECK, "gpkg.tables-several", "error",
                   f"{path} holds {len(tables)} feature tables ({', '.join(tables)}) but its row "
                   "lists no `tables`: an untabled row holds exactly one, which a reader reads "
                   "whatever it is named and never picks out of several; list the tables, or "
                   "split the file", pointer=pointer, file=path)
    elif not tables:
        report.add(_CHECK, "gpkg.table-none", "error",
                   f"{path} holds no feature table but its row lists no `tables`: an untabled "
                   "row holds exactly one", pointer=pointer, file=path)


def _feature_tables(bundle: Bundle, path: str) -> list[str] | None:
    handle, scratch = tempfile.mkstemp(prefix="mpb-gpkg-", suffix=".gpkg")
    try:
        with os.fdopen(handle, "wb") as out:
            with bundle.open(path) as stream:
                while chunk := stream.read(1 << 20):
                    out.write(chunk)
        connection = sqlite3.connect(f"file:{scratch.replace(os.sep, '/')}?mode=ro", uri=True)
        try:
            return [name for (name,) in connection.execute(_FEATURE_TABLES)]
        finally:
            connection.close()
    except (sqlite3.Error, OSError):
        return None
    finally:
        try:
            os.unlink(scratch)
        except OSError:
            pass
