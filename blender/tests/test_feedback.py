# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""HPS-56: the Send Feedback link, one parameter rule per test."""

import unittest
from urllib.parse import parse_qs, urlsplit

from mantleplace_blender.core.feedback import feedback_url


def query(url):
    parts = urlsplit(url)
    return parts.scheme + "://" + parts.netloc + parts.path, parse_qs(parts.query)


class FeedbackUrlTests(unittest.TestCase):
    def test_the_general_action_names_the_host_and_both_versions(self):
        base, params = query(feedback_url("0.1.0", "5.2.0"))
        self.assertEqual(base, "https://mantle.place/feedback")
        self.assertEqual(params, {"source": ["dcc-blender"], "v": ["0.1.0"], "hv": ["5.2.0"]})

    def test_the_general_action_sends_no_order(self):
        self.assertNotIn("order", query(feedback_url("0.1.0", "5.2.0"))[1])

    def test_a_malformed_plugin_version_is_left_out_never_sent_raw(self):
        for bad in ("0.1.0-dev", "v0.1", "", "1.2.3.4.5", "0.1 beta", None):
            with self.subTest(bad=bad):
                params = query(feedback_url(bad, "5.2.0"))[1]
                self.assertNotIn("v", params)
                self.assertEqual(params["hv"], ["5.2.0"])

    def test_a_malformed_host_version_is_left_out(self):
        # bpy.app.version_string reads "5.2.0 LTS" on a long-term release; the suffix is not a
        # version, so the shim passes the numeric tuple and anything else is dropped here.
        for bad in ("5.2.0 LTS", "4.5.3 Alpha", "", None):
            with self.subTest(bad=bad):
                self.assertNotIn("hv", query(feedback_url("0.1.0", bad))[1])

    def test_a_report_on_one_bundle_carries_its_order_id(self):
        order = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b"
        self.assertEqual(query(feedback_url("0.1.0", "5.2.0", order_id=order))[1]["order"], [order])

    def test_an_order_id_that_is_not_a_uuid_is_left_out(self):
        params = query(feedback_url("0.1.0", "5.2.0", order_id="someone@example.com"))[1]
        self.assertNotIn("order", params)

    def test_the_import_prompt_sends_its_rating_as_up_or_down(self):
        order = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b"
        for rating in ("up", "down"):
            with self.subTest(rating=rating):
                params = query(feedback_url("0.1.0", "5.2.0", order_id=order, rating=rating))[1]
                self.assertEqual(params["rating"], [rating])
                self.assertEqual(params["order"], [order])

    def test_a_rating_that_is_not_exactly_up_or_down_is_left_out(self):
        for bad in ("UP", "Down", "yes", "no", "1", "", " up", "up ", "up&x=1", "up\n", None, 1, True):
            with self.subTest(bad=bad):
                self.assertNotIn("rating", query(feedback_url("0.1.0", "5.2.0", rating=bad))[1])

    def test_the_general_action_sends_no_rating(self):
        self.assertNotIn("rating", query(feedback_url("0.1.0", "5.2.0"))[1])

    def test_no_parameter_outside_the_five_is_ever_emitted(self):
        url = feedback_url("0.1.0&email=a@b.c", "5.2.0", order_id="x&name=me", rating="up&token=1")
        self.assertEqual(set(query(url)[1]), {"source", "hv"})
        url = feedback_url("0.1.0", "5.2.0", "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b", "down")
        self.assertEqual(set(query(url)[1]), {"source", "v", "hv", "order", "rating"})


if __name__ == "__main__":
    unittest.main()
