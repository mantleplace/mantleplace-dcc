# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import os
import re
import zipfile
from pathlib import Path
from typing import IO

from .report import Report

MANIFEST_PATH = "Metadata/manifest.json"
MANIFEST_PATH_3 = "Manifest.json"
MANIFEST_PATHS = (MANIFEST_PATH_3, MANIFEST_PATH)

_IGNORED_FOLDERS = frozenset({"__MACOSX"})
_IGNORED_FILES = frozenset({".DS_Store"})
_DRIVE = re.compile(r"^[A-Za-z]:")


def ignored(name: str) -> bool:
    parts = name.rstrip("/").split("/")
    return any(p in _IGNORED_FOLDERS for p in parts) or parts[-1] in _IGNORED_FILES


def unsafe(name: str) -> bool:
    if not name or name.startswith("/") or "\\" in name or _DRIVE.match(name) or "\x00" in name:
        return True
    parts = name[:-1].split("/") if name.endswith("/") else name.split("/")
    return any(p in ("", ".", "..") for p in parts)


class Bundle:
    def __init__(self, form: str, root: str, files: dict[str, object],
                 archive: zipfile.ZipFile | None, manifest_path: str = MANIFEST_PATH) -> None:
        self.form = form
        self.root = root
        self.manifest_path = manifest_path
        self._files = files
        self._archive = archive
        self.folders = {"/".join(p.split("/")[:i]) for p in files for i in range(1, p.count("/") + 1)}
        self._folded: dict[str, str] | None = None

    @property
    def files(self) -> list[str]:
        return sorted(self._files)

    def members(self) -> list[tuple[str, zipfile.ZipInfo]]:
        if self._archive is None:
            return []
        return [(path, self._files[path]) for path in self.files]  # type: ignore[misc]

    def has_file(self, path: str) -> bool:
        return path in self._files

    def has_folder(self, path: str) -> bool:
        return path.rstrip("/") in self.folders

    def resolve(self, path: str, fold_case: bool = False) -> str | None:
        bare = path.rstrip("/")
        if path in self._files:
            return path
        if bare in self.folders:
            return bare
        if not fold_case:
            return None
        if self._folded is None:
            self._folded = {}
            for entry in sorted(self.folders) + self.files:
                self._folded.setdefault(entry.casefold(), entry)
        return self._folded.get(bare.casefold())

    def size(self, path: str) -> int:
        handle = self._files[path]
        if self._archive is not None:
            return handle.file_size  # type: ignore[attr-defined]
        return handle.stat().st_size  # type: ignore[attr-defined]

    def open(self, path: str) -> IO[bytes]:
        handle = self._files[path]
        if self._archive is not None:
            return self._archive.open(handle)  # type: ignore[arg-type]
        return open(handle, "rb")  # type: ignore[arg-type]

    def read(self, path: str) -> bytes:
        with self.open(path) as stream:
            return stream.read()

    def close(self) -> None:
        if self._archive is not None:
            self._archive.close()

    def __enter__(self) -> "Bundle":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def open_bundle(path: Path, report: Report) -> Bundle | None:
    report.ran.append("archive")
    entries: dict[str, object] = {}
    archive: zipfile.ZipFile | None = None
    if path.is_dir():
        form = "folder"
        for folder, subfolders, names in os.walk(path):
            subfolders[:] = sorted(d for d in subfolders if d not in _IGNORED_FOLDERS)
            for name in sorted(names):
                full = Path(folder) / name
                relative = full.relative_to(path).as_posix()
                if not ignored(relative) and full.is_file():
                    entries[relative] = full
    elif path.is_file() and zipfile.is_zipfile(path):
        form = "zip"
        try:
            archive = zipfile.ZipFile(path)
            infos = archive.infolist()
        except (OSError, zipfile.BadZipFile) as exc:
            report.cannot_check("archive", "archive.unreadable", f"{path} cannot be read as a zip: {exc}")
            return None
        for info in infos:
            name = info.filename
            if ignored(name) or name.endswith("/"):
                continue
            if unsafe(name):
                report.add("archive", "archive.unsafe-entry", "error",
                           f"zip entry {name!r} is not a plain relative path inside the bundle; "
                           "a reader must refuse it rather than write it", file=name)
                continue
            if name in entries:
                report.add("archive", "archive.duplicate-entry", "error",
                           f"zip entry {name!r} appears more than once; which copy a reader gets is "
                           "undefined", file=name)
                continue
            entries[name] = info
    else:
        what = "does not exist" if not path.exists() else "is neither a folder nor a zip"
        report.cannot_check("archive", "archive.unreadable", f"{path} {what}")
        return None

    found = _root(entries)
    if found is None:
        report.add("archive", "archive.no-manifest", "error",
                   f"no {MANIFEST_PATH_3} (3.x) or {MANIFEST_PATH} (2.x and 1.x) at the top of the "
                   "bundle, or inside one wrapper folder; it is the one file a reader finds by "
                   "convention")
        if archive is not None:
            archive.close()
        return None
    root, manifest_path = found
    if root:
        report.add("archive", "archive.wrapped", "info",
                   f"the bundle sits inside the wrapper folder {root!r}; paths are read from there")
    files = {name[len(root):]: handle for name, handle in entries.items() if name.startswith(root)}
    report.form, report.root, report.manifest_path = form, root, manifest_path
    return Bundle(form, root, files, archive, manifest_path)


def _root(entries: dict[str, object]) -> tuple[str, str] | None:
    for manifest_path in MANIFEST_PATHS:
        if manifest_path in entries:
            return "", manifest_path
    tops = {name.split("/", 1)[0] for name in entries}
    if len(tops) == 1 and all("/" in name for name in entries):
        wrapper = tops.pop() + "/"
        for manifest_path in MANIFEST_PATHS:
            if wrapper + manifest_path in entries:
                return wrapper, manifest_path
    return None
