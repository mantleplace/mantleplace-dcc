# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import zlib
import zipfile

from .archive import Bundle
from .report import Report

_METHODS = {zipfile.ZIP_STORED: "stored", zipfile.ZIP_DEFLATED: "deflate",
            zipfile.ZIP_BZIP2: "bzip2", zipfile.ZIP_LZMA: "lzma"}


def method_name(method: int) -> str:
    return _METHODS.get(method, f"method {method}")


def _shrinks(bundle: Bundle, path: str) -> bool:
    packer = zlib.compressobj(zlib.Z_DEFAULT_COMPRESSION, zlib.DEFLATED, -15)
    size = bundle.size(path)
    packed = 0
    with bundle.open(path) as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            packed += len(packer.compress(chunk))
    packed += len(packer.flush())
    return packed < size


def check_zip(bundle: Bundle, report: Report) -> None:
    members = bundle.members()
    if not members:
        return
    report.ran.append("zip")
    for path, info in members:
        if info.compress_type == zipfile.ZIP_DEFLATED:
            continue
        if info.compress_type == zipfile.ZIP_STORED:
            if not _shrinks(bundle, path):
                continue
            report.add("zip", "zip.compression", "error",
                       f"{path} is stored, but DEFLATE would make it smaller; a member is "
                       "DEFLATE-compressed, or stored where compression does not shrink it",
                       file=path)
            continue
        report.add("zip", "zip.compression", "error",
                   f"{path} is compressed with {method_name(info.compress_type)}; a member is "
                   "DEFLATE-compressed, or stored where compression does not shrink it", file=path)
