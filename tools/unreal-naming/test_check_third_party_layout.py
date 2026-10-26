#!/usr/bin/env python3
"""Tests for the third-party layout gate.

Each test builds a throwaway tree rather than asserting against the real one, so the suite says what
the gate MEANS. The passing cases matter as much as the failing ones: a gate that fires on the
plugin's own source gets silenced wholesale, and then it guards nothing.
"""

from __future__ import annotations

import io
import shutil
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from tempfile import TemporaryDirectory

import check_third_party_layout as gate

PLUGIN = Path("unreal/MantlePlace")

OFL_TEXT = (
    "Copyright 2024 The Example Project Authors\n\n"
    "This Font Software is licensed under the SIL Open Font License, Version 1.1.\n"
)


class LayoutTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

    def reset(self) -> None:
        shutil.rmtree(self.root / PLUGIN, ignore_errors=True)

    def write(self, relative: str, body: str | bytes = "") -> Path:
        path = self.root / PLUGIN / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(body, bytes):
            path.write_bytes(body)
        else:
            path.write_text(body, encoding="utf-8")
        return path

    def run_gate(self) -> tuple[int, str]:
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            code = gate.run(self.root)
        return code, buffer.getvalue()


class PassingLayouts(LayoutTestCase):
    def test_fonts_and_licences_under_third_party_pass(self) -> None:
        self.write("Source/ThirdParty/Fonts/OFL-Montserrat.txt", OFL_TEXT)
        self.write("Source/ThirdParty/Fonts/Montserrat-Medium.ttf", b"\x00\x01\x00\x00")
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_own_source_and_icons_pass(self) -> None:
        self.write("Source/MantlePlaceEditor/Private/Thing.cpp", "// Licensed under the Apache License\n")
        self.write("Resources/Icon128.png", b"\x89PNG")
        self.write("MantlePlace.uplugin", "{}")
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_source_that_merely_mentions_a_licence_word_passes(self) -> None:
        # "limit" and "admit" contain the letters, and a code comment is not a licence text.
        self.write("Source/MantlePlaceRuntime/Private/Limits.cpp", "// admit the limit; BSD sockets\n")
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_filter_plugin_without_licence_or_policy_files_passes(self) -> None:
        self.write("Config/FilterPlugin.ini", "; none carried\n[FilterPlugin]\n/Docs/Guide.pdf\n")
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_missing_plugin_folder_passes(self) -> None:
        code, _ = self.run_gate()
        self.assertEqual(code, 0)


class FailingLayouts(LayoutTestCase):
    def test_old_resources_fonts_layout_fails(self) -> None:
        self.write("Resources/Fonts/OFL-Montserrat.txt", OFL_TEXT)
        self.write("Resources/Fonts/Montserrat-Medium.ttf", b"\x00\x01\x00\x00")
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("Resources/Fonts/OFL-Montserrat.txt", out)

    def test_licence_file_names_fail(self) -> None:
        for name in ("OFL.txt", "LICENSE", "LICENSE.md", "COPYING", "NOTICE.txt", "license.txt"):
            with self.subTest(name=name):
                self.reset()
                self.write(f"Content/{name}", "x\n")
                code, out = self.run_gate()
                self.assertEqual(code, 1, out)

    def test_text_naming_a_licence_fails_under_any_name(self) -> None:
        cases = {
            "ofl.txt": "licensed under the SIL Open Font License, Version 1.1",
            "terms.txt": "The MIT License (MIT)\nPermission is hereby granted",
            "terms.md": "Redistribution and use in source and binary forms: BSD 3-Clause",
            "gpl.txt": "released under the GPL",
        }
        for name, body in cases.items():
            with self.subTest(name=name):
                self.reset()
                self.write(f"Resources/{name}", body)
                code, out = self.run_gate()
                self.assertEqual(code, 1, out)

    def test_third_party_folder_must_be_directly_under_source(self) -> None:
        self.write("Source/MantlePlaceEditor/ThirdParty/OFL.txt", OFL_TEXT)
        code, _ = self.run_gate()
        self.assertEqual(code, 1)

    def test_a_third_party_folder_outside_source_fails(self) -> None:
        self.write("ThirdParty/OFL.txt", OFL_TEXT)
        code, _ = self.run_gate()
        self.assertEqual(code, 1)

    def test_filter_plugin_listing_a_licence_or_policy_file_fails(self) -> None:
        for entry in ("/LICENSE", "/TRADEMARK.md", "/NOTICE.txt", "/COPYING", "/EULA.txt",
                      "/CODE_OF_CONDUCT.md", "license.md"):
            with self.subTest(entry=entry):
                self.reset()
                self.write("Config/FilterPlugin.ini", f"[FilterPlugin]\n{entry}\n")
                code, out = self.run_gate()
                self.assertEqual(code, 1, out)
                self.assertIn("FilterPlugin.ini", out)

    def test_filter_plugin_comment_naming_a_licence_file_passes(self) -> None:
        self.write("Config/FilterPlugin.ini", "; no LICENSE here\n[FilterPlugin]\n")
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_each_offender_is_reported(self) -> None:
        self.write("Resources/Fonts/OFL-A.txt", OFL_TEXT)
        self.write("Content/NOTICE", "x")
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("OFL-A.txt", out)
        self.assertIn("NOTICE", out)


if __name__ == "__main__":
    unittest.main()
