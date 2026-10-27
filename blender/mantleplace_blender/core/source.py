# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""A bundle on disk, as its zip or as an extracted folder, and one file taken out of it.

Plain file I/O and the standard library, no Blender, so the headless suite runs it on any machine.
The rules come from spec/format.md: the manifest is the one file found by its fixed name (§3), a
manifest path matches an archive entry regardless of case (§10.3, rule 6), an archive path is
untrusted and never leaves the extraction root (§3), and a stated sha256 is verified and fails
closed while an absent one means unknown, never corrupt (§5).
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import tempfile
import zipfile

from .bundle import BundleRefused

MANIFEST_PATH = "Metadata/manifest.json"
_CHUNK = 1 << 20


def open_bundle(path: str) -> "ZipBundle | FolderBundle":
    """Open the bundle a curator picked: its `.zip`, or the `manifest.json` of an extracted copy."""
    lowered = path.lower()
    if lowered.endswith(".zip"):
        return ZipBundle(path)
    if lowered.endswith(".json"):
        return FolderBundle(path)
    raise BundleRefused("Pick the bundle's .zip, or the Metadata/manifest.json of an extracted "
                        "bundle.")


class _Bundle:
    manifest: dict

    def __enter__(self):
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def close(self) -> None:
        pass


class ZipBundle(_Bundle):
    def __init__(self, path: str) -> None:
        try:
            self._zip = zipfile.ZipFile(path)
        except (OSError, zipfile.BadZipFile) as exc:
            raise BundleRefused(f"{os.path.basename(path)} is not a readable zip ({exc}).") from exc
        self._names = {name.lower(): name for name in self._zip.namelist()}
        entry = self._names.get(MANIFEST_PATH.lower())
        if entry is None:
            self._zip.close()
            raise BundleRefused(f"{os.path.basename(path)} has no {MANIFEST_PATH}, so it is not a "
                                "Mantle Place bundle.")
        self.manifest = _parse(self._zip.read(entry), MANIFEST_PATH)

    def close(self) -> None:
        self._zip.close()

    def materialise(self, archive_path: str, out_dir: str,
                    sha256: str | None) -> tuple[str, bool]:
        """Extract one entry under `out_dir`, verified. Returns the path and whether a digest
        was checked. The bytes go to a temporary name first and are renamed only once verified,
        so a refused file leaves nothing a later import could mistake for good."""
        entry = self._names.get(archive_path.lower())
        if entry is None:
            raise BundleRefused(f"The manifest points at {archive_path}, and the zip does not "
                                "hold it.")
        target = _inside(out_dir, archive_path)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        digest = hashlib.sha256()
        handle, partial = tempfile.mkstemp(dir=os.path.dirname(target), suffix=".part")
        try:
            with os.fdopen(handle, "wb") as sink, self._zip.open(entry) as source:
                for chunk in iter(lambda: source.read(_CHUNK), b""):
                    digest.update(chunk)
                    sink.write(chunk)
            _verify(archive_path, digest.hexdigest(), sha256)
            os.replace(partial, target)
        except BaseException:
            if os.path.exists(partial):
                os.remove(partial)
            raise
        return target, sha256 is not None


class FolderBundle(_Bundle):
    def __init__(self, manifest_path: str) -> None:
        try:
            with open(manifest_path, "rb") as handle:
                self.manifest = _parse(handle.read(), os.path.basename(manifest_path))
        except OSError as exc:
            raise BundleRefused(f"{manifest_path} cannot be read ({exc}).") from exc
        # The manifest sits at its fixed name, so the bundle root is two folders up.
        self._root = os.path.dirname(os.path.dirname(os.path.abspath(manifest_path)))

    def materialise(self, archive_path: str, out_dir: str,
                    sha256: str | None) -> tuple[str, bool]:
        """Find one file in the extracted bundle, verified. It is read in place, never copied."""
        _inside(out_dir, archive_path)  # the same escape guard as the zip path
        found = _find(self._root, archive_path)
        if found is None:
            raise BundleRefused(f"The manifest points at {archive_path}, and the extracted bundle "
                                "does not hold it.")
        if sha256 is not None:
            digest = hashlib.sha256()
            with open(found, "rb") as handle:
                for chunk in iter(lambda: handle.read(_CHUNK), b""):
                    digest.update(chunk)
            _verify(archive_path, digest.hexdigest(), sha256)
        return found, sha256 is not None


def _parse(data: bytes, name: str) -> dict:
    try:
        manifest = json.loads(data.decode("utf-8-sig"))
    except (UnicodeDecodeError, ValueError) as exc:
        raise BundleRefused(f"{name} is not readable JSON ({exc}).") from exc
    if not isinstance(manifest, dict) or "version" not in manifest:
        raise BundleRefused(f"{name} is not a Mantle Place manifest.")
    return manifest


def _verify(archive_path: str, actual: str, expected: str | None) -> None:
    if expected is not None and actual != expected.lower():
        raise BundleRefused(f"{archive_path} does not match the sha256 its manifest states, so it "
                            "was not imported. Download the bundle again.")


def _inside(root: str, archive_path: str) -> str:
    root = os.path.abspath(root)
    target = os.path.abspath(os.path.join(root, *archive_path.replace("\\", "/").split("/")))
    if os.path.commonpath([root, target]) != root or target == root:
        raise BundleRefused(f"{archive_path} points outside the bundle; refusing it.")
    return target


def _find(root: str, archive_path: str) -> str | None:
    """The file at `archive_path` under `root`, each part matched regardless of case."""
    here = root
    for part in archive_path.replace("\\", "/").split("/"):
        exact = os.path.join(here, part)
        if os.path.exists(exact):
            here = exact
            continue
        try:
            match = next((n for n in os.listdir(here) if n.lower() == part.lower()), None)
        except OSError:
            return None
        if match is None:
            return None
        here = os.path.join(here, match)
    return here if os.path.isfile(here) else None


def remove_tree(path: str) -> None:
    """Remove a scratch folder the import made, ignoring a file the OS still holds open."""
    shutil.rmtree(path, ignore_errors=True)
