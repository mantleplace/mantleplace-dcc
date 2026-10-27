#!/usr/bin/env python3
"""Tests for the vocabulary gate.

Each test builds a throwaway git repository with its own small glossary, so the suite says what the
gate MEANS rather than what this repository's Avoid list holds today -- except one test, which runs
the gate on the real tree, because "passes on main" is part of the contract.

Both directions are asserted for every rule. A gate that flags a code identifier, a quoted mention
in the glossary or "its own" is a gate somebody switches off, and then the list guards nothing.
"""

from __future__ import annotations

import io
import subprocess
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from tempfile import TemporaryDirectory

import check_vocabulary as gate

GLOSSARY = """\
# Fixture glossary

**Curator**:
Someone who keeps a bundle. Its own files are theirs to keep.
_Avoid_: user

## Avoid list

The banned-term list.

| Banned | Use instead |
| --- | --- |
| "own", "owned" and "ownership", said about bundles | keep wording: a curator *keeps* it |
| "download-to-own" | "download and keep" |
| "Fixture Importer" | "Fixture for Unreal" |
"""


class GateTestCase(unittest.TestCase):
    """A temporary git repository, because the gate reads tracked files and nothing else."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)
        subprocess.run(["git", "init", "-q"], cwd=self.root, check=True)
        self.write("CONTEXT.md", GLOSSARY)

    def write(self, relative: str, body: str) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
        return path

    def run_gate(self) -> tuple[int, str]:
        subprocess.run(["git", "add", "-A"], cwd=self.root, check=True)
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            code = gate.run(self.root)
        return code, buffer.getvalue()

    def assertClean(self) -> None:
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def assertRefused(self, *expected: str) -> str:
        code, out = self.run_gate()
        self.assertEqual(code, 1, out)
        for fragment in expected:
            self.assertIn(fragment, out)
        return out


class BaselineTests(GateTestCase):
    def test_a_tree_with_nothing_banned_passes(self) -> None:
        self.write("README.md", "# Fixture\n\nA curator downloads a bundle and keeps it.\n")
        self.assertClean()

    def test_the_real_repository_is_clean(self) -> None:
        """The gate is a regression guard, so it starts from zero findings on this tree."""
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            code = gate.run(gate.REPO_ROOT)
        self.assertEqual(code, 0, buffer.getvalue())


class DocumentTests(GateTestCase):
    def test_a_banned_term_in_a_doc_is_refused_with_file_line_and_replacement(self) -> None:
        self.write("docs/notes.md", "# Notes\n\nSome prose.\n\nYou can own a bundle for good.\n")
        out = self.assertRefused("docs/notes.md:5:", '"own"', "keep wording: a curator *keeps* it")
        self.assertIn("said about bundles", out)

    def test_a_banned_term_in_a_readme_is_refused(self) -> None:
        self.write("revit/README.md", "Download-to-own, as the old copy put it.\n")
        self.assertRefused("revit/README.md:1:", '"Download-to-own"', '"download and keep"')

    def test_a_phrase_broken_across_a_line_wrap_is_refused(self) -> None:
        self.write("README.md", "Install the Fixture\nImporter from the releases page.\n")
        self.assertRefused("README.md:1:", "Fixture for Unreal")

    def test_matching_ignores_case_and_wants_a_whole_word(self) -> None:
        self.write("a.md", "OWNERSHIP of a bundle is not what a curator gets.\n")
        self.assertRefused("a.md:1:")
        self.write("a.md", "The ownerId and the downtown bundle are fine.\n")
        self.assertClean()

    def test_every_form_in_the_row_is_refused(self) -> None:
        self.write("a.md", "The bundle is owned.\n\nThe bundle's ownership is unclear.\n")
        out = self.assertRefused("a.md:1:", "a.md:3:")
        self.assertIn('"owned"', out)
        self.assertIn('"ownership"', out)

    def test_a_qualified_term_is_refused_only_beside_its_subject(self) -> None:
        self.write("a.md", "The corpus is maintainer-owned. Nothing owned here.\n")
        self.assertClean()
        self.write("a.md", "We own the schema. The bundle is a zip.\n")
        self.assertClean()

    def test_the_possessive_adjective_is_not_ownership(self) -> None:
        self.write(
            "a.md",
            "A bundle states its own frame, and a host's own files, and **their own** hash.\n"
            "This host's\n**own three** cases read the bundle.\n",
        )
        self.assertClean()

    def test_an_unqualified_row_is_refused_everywhere(self) -> None:
        self.write("a.md", "Nothing about archives here, except download-to-own.\n")
        self.assertRefused("a.md:1:")

    def test_code_is_exempt(self) -> None:
        self.write(
            "a.md",
            "The `ownership` field of a bundle and `download-to-own`.\n\n"
            "```python\nbundle.owned = True  # download-to-own\n```\n",
        )
        self.assertClean()

    def test_a_link_target_is_an_address_not_prose(self) -> None:
        self.write(
            "a.md",
            "See [the bundle](docs/owned-bundles.md) and https://example.test/owned/bundle.\n"
            "\n[ref]: docs/ownership-of-a-bundle.md\n",
        )
        self.assertClean()


class GlossaryTests(GateTestCase):
    def test_the_glossarys_own_avoid_list_does_not_trip_it(self) -> None:
        self.assertClean()

    def test_a_quoted_mention_in_the_glossary_is_allowed(self) -> None:
        self.write("CONTEXT.md", GLOSSARY.replace("keeps a bundle.", 'keeps a bundle, never "owns" it, never "owned".'))
        self.assertClean()

    def test_an_unquoted_use_in_the_glossary_is_refused(self) -> None:
        self.write("CONTEXT.md", GLOSSARY.replace("keeps a bundle.", "owned a bundle."))
        self.assertRefused("CONTEXT.md:4:")

    def test_the_avoid_list_is_read_not_copied(self) -> None:
        self.write("a.md", "Run the thing in Fixture Mode.\n")
        self.assertClean()
        self.write("CONTEXT.md", GLOSSARY + '| "Fixture Mode" | "Fixture Run" |\n')
        self.assertRefused("a.md:1:", "Fixture Mode", '"Fixture Run"')

    def test_a_glossary_with_no_readable_list_refuses_rather_than_passing_everything(self) -> None:
        self.write("CONTEXT.md", "# Fixture glossary\n\nNo list here.\n")
        code, out = self.run_gate()
        self.assertEqual(code, 1, out)
        self.assertIn("no banned term could be read", out)

    def test_a_missing_glossary_refuses(self) -> None:
        (self.root / "CONTEXT.md").unlink()
        code, out = self.run_gate()
        self.assertEqual(code, 1, out)
        self.assertIn("CONTEXT.md is missing", out)


class HostStringTests(GateTestCase):
    def test_a_banned_term_in_a_csharp_string_is_refused(self) -> None:
        self.write(
            "revit/src/Addin/Notice.cs",
            'class Notice\n{\n    string Text = "This bundle is owned by you.";\n}\n',
        )
        self.assertRefused("revit/src/Addin/Notice.cs:3:", '"owned"')

    def test_a_banned_term_in_an_unreal_string_is_refused(self) -> None:
        self.write(
            "unreal/Plugin/Source/Panel.cpp",
            '#define LOCTEXT_NAMESPACE "Panel"\n'
            'auto Label = LOCTEXT("Key", "Open the Fixture Importer");\n',
        )
        self.assertRefused("unreal/Plugin/Source/Panel.cpp:2:", "Fixture for Unreal")

    def test_a_banned_term_in_a_python_string_is_refused(self) -> None:
        self.write("unreal/Plugin/Content/Python/tool.py", 'print("Your bundle: download-to-own")\n')
        self.assertRefused("unreal/Plugin/Content/Python/tool.py:1:")

    def test_a_banned_term_in_a_blender_string_is_refused(self) -> None:
        self.write("blender/mantleplace_blender/shim.py", 'label = "Keep the bundle you own"\n')
        self.assertRefused("blender/mantleplace_blender/shim.py:1:")

    def test_a_verbatim_string_reports_the_line_of_the_term(self) -> None:
        self.write("revit/src/Addin/Notice.cs", 'class N { string T = @"one\nthe bundle ownership\ntwo"; }\n')
        self.assertRefused("revit/src/Addin/Notice.cs:2:")

    def test_code_identifiers_are_exempt(self) -> None:
        self.write(
            "revit/src/Addin/Bundle.cs",
            "class Bundle\n{\n    bool IsOwned;\n    string ownership = \"ownership\";\n"
            '    string Key = "bundle.owned";\n    string Url = "owned/bundle";\n}\n',
        )
        self.assertClean()

    def test_a_comment_is_not_a_string(self) -> None:
        self.write(
            "revit/src/Addin/Bundle.cs",
            "// The bundle is owned by Revit's window.\n"
            "/* a bundle: download-to-own */\n"
            "class Bundle { /// <summary>Owned bundle.</summary>\n}\n",
        )
        self.assertClean()

    def test_a_quote_inside_a_comment_or_char_does_not_open_a_string(self) -> None:
        self.write(
            "revit/src/Addin/Bundle.cs",
            "// a \" stray quote\n"
            "class Bundle { char Quote = '\"'; string Fine = \"keeps the bundle\"; }\n",
        )
        self.assertClean()

    def test_a_test_file_is_not_a_host_string(self) -> None:
        self.write("revit/tests/Core.Tests/BundleTests.cs", 'class T { string N = "an owned bundle stamp"; }\n')
        self.write("unreal/Plugin/Source/Tests/Spec.cpp", 'auto N = TEXT("an owned bundle stamp");\n')
        self.write("unreal/tools/test_manifest.py", 'print("an owned bundle stamp")\n')
        self.assertClean()

    def test_text_outside_the_host_roots_is_not_scanned_as_code(self) -> None:
        self.write("tools/thing/script.py", 'print("an owned bundle")\n')
        self.assertClean()


if __name__ == "__main__":
    unittest.main()
