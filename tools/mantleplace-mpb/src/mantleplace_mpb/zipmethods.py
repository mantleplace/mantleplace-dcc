# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""How each member of a 3.x zip is compressed (`conformance.md` §7, `format.md` §10).

A member is DEFLATE-compressed, or stored where compression does not shrink it. A member in any
other method (BZIP2, LZMA, and so on) is `zip.compression`, and so is one that is stored when
DEFLATE would have made it smaller. This reads the archive's own member records, which an extracted
tree no longer has, so a folder is not checked: the check is `skipped` for one.
"""

from __future__ import annotations

import zlib
import zipfile

from .archive import Bundle
from .report import Report

#: The names of the zip methods a producer meets, for the message.
_METHODS = {zipfile.ZIP_STORED: "stored", zipfile.ZIP_DEFLATED: "deflate",
            zipfile.ZIP_BZIP2: "bzip2", zipfile.ZIP_LZMA: "lzma"}


def method_name(method: int) -> str:
    return _METHODS.get(method, f"method {method}")


def _shrinks(bundle: Bundle, path: str) -> bool:
    """Whether DEFLATE, at its default level, would make the member `path` smaller."""
    packer = zlib.compressobj(zlib.Z_DEFAULT_COMPRESSION, zlib.DEFLATED, -15)
    size = bundle.size(path)
    packed = 0
    with bundle.open(path) as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            packed += len(packer.compress(chunk))
    packed += len(packer.flush())
    return packed < size


def check_zip(bundle: Bundle, report: Report) -> None:
    """Every member of a zip is DEFLATE-compressed, or stored where DEFLATE would not shrink it."""
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
