# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""`HPS-56`: the one link Send Feedback opens. The plugin carries no feedback form of its own."""

from __future__ import annotations

import re
from urllib.parse import urlencode

FEEDBACK_URL = "https://mantle.place/feedback"
#: `dcc-` plus this host's directory name.
SOURCE = "dcc-blender"
#: The only two values `rating` may take: the answer to "Did this import come in right?".
RATINGS = ("up", "down")

_VERSION = re.compile(r"^[0-9]+(\.[0-9]+){0,3}$")
_UUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


def feedback_url(
    addon_version: object,
    blender_version: object,
    order_id: object = None,
    rating: object = None,
) -> str:
    """The feedback link. A version that is not a plain version string is left out, never sent
    raw; an order id is sent only by a bundle-scoped action, and only when it is a UUID; a rating
    is sent only by the post-import prompt, and only as exactly `up` or `down`."""
    params = [("source", SOURCE)]
    if isinstance(addon_version, str) and _VERSION.match(addon_version):
        params.append(("v", addon_version))
    if isinstance(blender_version, str) and _VERSION.match(blender_version):
        params.append(("hv", blender_version))
    if isinstance(order_id, str) and _UUID.match(order_id):
        params.append(("order", order_id))
    if isinstance(rating, str) and rating in RATINGS:
        params.append(("rating", rating))
    return f"{FEEDBACK_URL}?{urlencode(params)}"
