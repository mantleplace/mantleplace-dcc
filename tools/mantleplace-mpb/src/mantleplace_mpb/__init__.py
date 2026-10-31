# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""mantleplace-mpb: validate a Mantle Place Bundle (MPB) against the open format.

    from mantleplace_mpb import validate
    report = validate("site.zip")
    report.verdict      # "pass", "fail" or "error"
    report.to_dict()    # the machine-readable report `mpb validate --json` prints

    from mantleplace_mpb import read_bundle
    with read_bundle("site.zip") as bundle:
        for f in bundle.files:
            f.path, f.frame, f.role     # every file, its frame (or None) and its ODbL role
"""

from __future__ import annotations

#: MAJOR.MINOR.PATCH is the MPB manifest version the conformance corpus is written against;
#: `.postN` marks a release that changes this package and not the suite. `release_check.py` and
#: the tests hold it there.
__version__ = "1.18.0"

from .reader import (  # noqa: E402
    BundleError,
    BundleFile,
    BundleReader,
    Frame,
    FrameError,
    FrameGroup,
    FrameMismatch,
    Offset,
    Placement,
    SiteOrigin,
    Transform,
    read_bundle,
)
from .report import Finding, Report  # noqa: E402
from .schemas import SchemaSource  # noqa: E402
from .validate import validate  # noqa: E402

__all__ = ["BundleError", "BundleFile", "BundleReader", "Finding", "Frame", "FrameError",
           "FrameGroup", "FrameMismatch", "Offset", "Placement", "Report", "SchemaSource",
           "SiteOrigin", "Transform", "__version__", "read_bundle", "validate"]
