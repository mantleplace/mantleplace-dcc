# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""mantleplace-mpb: validate a Mantle Place Bundle (MPB) against the open format.

    from mantleplace_mpb import validate
    report = validate("site.zip")
    report.verdict      # "pass", "fail" or "error"
    report.to_dict()    # the machine-readable report `mpb validate --json` prints
"""

from __future__ import annotations

__version__ = "0.1.0"

from .report import Finding, Report  # noqa: E402
from .schemas import SchemaSource  # noqa: E402
from .validate import validate  # noqa: E402

__all__ = ["Finding", "Report", "SchemaSource", "__version__", "validate"]
