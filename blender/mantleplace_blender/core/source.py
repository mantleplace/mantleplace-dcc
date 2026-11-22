# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import hashlib
import json
import os
import shutil
import tempfile
import time
import zipfile

from .bundle import BundleRefused

MANIFEST_PATH = "Metadata/manifest.json"
_CHUNK = 1 << 20


def open_bundle(path: str) -> "ZipBundle | FolderBundle":
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
        self._root = os.path.dirname(os.path.dirname(os.path.abspath(manifest_path)))

    def materialise(self, archive_path: str, out_dir: str,
                    sha256: str | None) -> tuple[str, bool]:
        _inside(out_dir, archive_path)
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


def remove_tree(path: str, attempts: int = 3, pause: float = 0.25, sleep=time.sleep) -> list[str]:
    for attempt in range(attempts):
        try:
            shutil.rmtree(path)
            return []
        except FileNotFoundError:
            break
        except OSError:
            if attempt + 1 < attempts:
                sleep(pause)
    return _left_behind(path)


def _left_behind(path: str) -> list[str]:
    if not os.path.exists(path):
        return []
    files = [os.path.join(folder, name) for folder, _, names in os.walk(path) for name in names]
    return files or [path]


def left_behind_note(paths: list[str]) -> str:
    return "Could not remove the import's scratch files; delete them by hand: " + ", ".join(paths)
