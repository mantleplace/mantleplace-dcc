#!/usr/bin/env python3
"""Tests for the documentation-integrity gate.

Each test builds a throwaway repository rather than asserting against the real tree, so the suite
says what the gate MEANS rather than what this repository happens to contain today.

The non-findings matter as much as the findings, and both directions are asserted for every rule.
A gate that flags an example command, an external URL or a README with no frontmatter is a gate
somebody switches off within a week — and then it guards nothing, which is the state that let six
stale claims reach main in the first place.
"""

from __future__ import annotations

import io
import subprocess
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from tempfile import TemporaryDirectory

import check_docs_integrity as gate

# The fixture root guide. Every test writes one, because the ADR index check reads it always: a
# tree whose guide is missing is itself a finding, which one test below asserts on purpose.
ROOT_GUIDE = """\
# Fixture guide

- **Why a decision was taken** → [`docs/adr/`](docs/adr/), numbered and append-only. Today:
  **0001** the first decision · **0002** the second one, which 0001's reasoning anticipated.
- **Something else** → nothing to check here.
"""


class GateTestCase(unittest.TestCase):
    """A temporary git repository, because the gate reads tracked files and nothing else."""

    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)
        subprocess.run(["git", "init", "-q"], cwd=self.root, check=True)
        self.write("CLAUDE.md", ROOT_GUIDE)
        self.write("docs/adr/0001-the-first-decision.md", self.document("adr-0001", "The first."))
        self.write("docs/adr/0002-the-second-decision.md", self.document("adr-0002", "The second."))

    def document(self, name: str, description: str, body: str = "\n# Body\n") -> str:
        return f"---\nname: {name}\ndescription: {description}\n---\n{body}"

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
    def test_a_tree_with_nothing_wrong_passes(self) -> None:
        self.assertClean()

    def test_the_real_repository_is_clean(self) -> None:
        """The gate is a regression guard, so it starts from zero findings on this tree."""
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            code = gate.run(gate.REPO_ROOT)
        self.assertEqual(code, 0, buffer.getvalue())


class LinkTests(GateTestCase):
    def test_a_link_to_a_missing_file_is_refused(self) -> None:
        self.write("README.md", "See [the notes](notes/missing.md).\n")
        out = self.assertRefused("README.md:1", "notes/missing.md")
        self.assertIn("link target does not exist", out)

    def test_a_link_from_a_subdirectory_resolves_relative_to_its_own_file(self) -> None:
        self.write("docs/guide.md", self.document("guide", "A guide.", "\n[up](../README.md)\n"))
        self.assertRefused("docs/guide.md", "../README.md")
        self.write("README.md", "# Readme\n")
        self.assertClean()

    def test_a_reference_definition_target_is_checked_too(self) -> None:
        self.write("README.md", "See [the notes][notes].\n\n[notes]: notes/missing.md\n")
        self.assertRefused("README.md:3", "notes/missing.md")

    def test_a_link_to_a_directory_resolves(self) -> None:
        self.write("README.md", "The [decisions](docs/adr/) live here.\n")
        self.assertClean()

    def test_a_fragment_is_split_off_and_the_file_half_is_what_is_checked(self) -> None:
        self.write("README.md", "See [a section](docs/adr/0001-the-first-decision.md#body).\n")
        self.assertClean()
        self.write("README.md", "See [a section](docs/adr/0009-nothing.md#body).\n")
        self.assertRefused("0009-nothing.md")

    def test_a_percent_encoded_target_is_decoded_before_it_is_resolved(self) -> None:
        self.write("docs/a file.md", self.document("a-file", "Spaces in the name."))
        self.write("README.md", "See [it](docs/a%20file.md).\n")
        self.assertClean()

    def test_a_target_whose_name_carries_parentheses_is_read_whole(self) -> None:
        """Cutting at the first ")" would report an existing file as missing."""
        self.write("docs/note (draft).md", self.document("note", "Parentheses in the name."))
        self.write("README.md", "See [it](docs/note%20(draft).md).\n")
        self.assertClean()

    def test_an_image_inside_a_link_yields_both_targets(self) -> None:
        self.write("README.md", "[![alt](icon.png)](docs/adr/0001-the-first-decision.md)\n")
        self.assertRefused("icon.png")
        self.write("icon.png", "not really a png\n")
        self.assertClean()
        self.write("README.md", "[![alt](icon.png)](docs/adr/0042-nope.md)\n")
        self.assertRefused("0042-nope.md")

    def test_a_site_root_relative_target_is_refused_even_when_the_file_exists(self) -> None:
        """GitHub resolves a leading slash against the site root, not this repository."""
        self.write("README.md", "See [it](/docs/adr/0001-the-first-decision.md).\n")
        self.assertRefused("site-root-relative", "/docs/adr/0001-the-first-decision.md")

    def test_a_target_whose_case_is_wrong_is_refused_on_every_platform(self) -> None:
        """`Path.exists()` is case-insensitive on Windows; the hosted runner is not."""
        self.write("README.md", "See [it](DOCS/adr/0001-the-first-decision.md).\n")
        self.assertRefused("DOCS/adr/0001-the-first-decision.md")


class NonFindingTests(GateTestCase):
    """The false positives that would get this gate switched off."""

    def test_a_link_inside_a_fenced_block_is_not_a_link(self) -> None:
        self.write("README.md", "Example:\n\n```md\n[text](nowhere/at/all.md)\n```\n")
        self.assertClean()

    def test_a_fence_of_more_backticks_survives_an_inner_fence(self) -> None:
        self.write(
            "README.md",
            "````md\n```\n[text](nowhere/at/all.md)\n```\n````\n",
        )
        self.assertClean()

    def test_a_tilde_fence_is_a_fence(self) -> None:
        self.write("README.md", "~~~\n[text](nowhere/at/all.md)\n~~~\n")
        self.assertClean()

    def test_a_link_inside_an_inline_code_span_is_not_a_link(self) -> None:
        self.write("README.md", "Write it as `[text](path/to/file.md)` in prose.\n")
        self.assertClean()

    def test_an_external_url_is_never_resolved(self) -> None:
        self.write(
            "README.md",
            "[a](https://mantle.place/x) [b](http://example.invalid/y) "
            "[c](mailto:nobody@mantle.place) [d](//example.invalid/z)\n",
        )
        self.assertClean()

    def test_a_bare_anchor_has_no_file_half(self) -> None:
        self.write("README.md", "Jump to [the body](#body).\n")
        self.assertClean()

    def test_a_footnote_definition_is_not_a_link_definition(self) -> None:
        self.write("README.md", "A claim.[^note]\n\n[^note]: prose that names no file at all\n")
        self.assertClean()

    def test_a_multi_backtick_span_closes_on_its_own_run(self) -> None:
        self.write("README.md", "``a ` b`` and [x](docs/adr/0001-the-first-decision.md)\n")
        self.assertClean()
        self.write("README.md", "``a ` b`` and [x](docs/adr/0042-nope.md)\n")
        self.assertRefused("0042-nope.md")

    def test_a_target_that_escapes_the_repository_root_is_a_host_route(self) -> None:
        """`../../security/advisories/new` is GitHub's route, not a path, and resolves nowhere."""
        self.write("README.md", "[Report privately](../../security/advisories/new).\n")
        self.write("SECURITY.md", "[Report privately](../../security/advisories/new).\n")
        self.assertClean()

    def test_the_escape_exemption_does_not_launder_a_link_inside_the_tree(self) -> None:
        self.write("docs/guide.md", self.document("guide", "A guide.", "\n[x](../docs/gone.md)\n"))
        self.assertRefused("docs/guide.md", "../docs/gone.md")


class FrontmatterTests(GateTestCase):
    def test_a_docs_file_with_both_keys_passes(self) -> None:
        self.write("docs/guide.md", self.document("guide", "What it is for."))
        self.assertClean()

    def test_a_docs_file_with_no_frontmatter_is_refused(self) -> None:
        self.write("docs/guide.md", "# Guide\n")
        self.assertRefused("docs/guide.md", "no frontmatter block")

    def test_a_docs_file_missing_description_is_refused(self) -> None:
        self.write("docs/guide.md", "---\nname: guide\n---\n\n# Guide\n")
        self.assertRefused("docs/guide.md", "missing `description`")

    def test_an_empty_value_counts_as_missing(self) -> None:
        self.write("docs/guide.md", "---\nname: guide\ndescription:\n---\n\n# Guide\n")
        self.assertRefused("docs/guide.md", "missing `description`")

    def test_frontmatter_that_does_not_parse_is_refused(self) -> None:
        self.write("docs/guide.md", "---\nname: guide\nthis is not a key\n---\n\n# Guide\n")
        self.assertRefused("docs/guide.md", "does not parse")

    def test_an_unclosed_block_is_refused_rather_than_taken_as_complete(self) -> None:
        """Both keys are present and the block still fails: an unterminated block is not a block."""
        self.write("docs/guide.md", "---\nname: guide\ndescription: x\n")
        self.assertRefused("docs/guide.md", "never closed")

    def test_prose_below_an_unclosed_block_is_reported_as_a_parse_failure(self) -> None:
        self.write("docs/guide.md", "---\nname: guide\ndescription: x\n\n# Guide\n")
        self.assertRefused("docs/guide.md", "does not parse")

    def test_extra_keys_are_none_of_the_gate_s_business(self) -> None:
        self.write("docs/guide.md", "---\nname: g\ndescription: d\nstatus: accepted\n---\n\n# G\n")
        self.assertClean()

    def test_a_list_valued_key_is_a_value_rather_than_a_parse_failure(self) -> None:
        self.write(
            "docs/guide.md",
            "---\nname: g\ndescription: d\nhosts:\n  - unreal\n  - revit\n---\n\n# G\n",
        )
        self.assertClean()

    def test_a_wrapped_description_is_still_a_description(self) -> None:
        self.write("docs/guide.md", "---\nname: g\ndescription:\n  wrapped onto the next line\n---\n")
        self.assertClean()

    def test_a_host_guide_is_in_scope_structurally(self) -> None:
        self.write("revit/CLAUDE.md", "# Revit\n")
        self.assertRefused("revit/CLAUDE.md", "no frontmatter block")
        self.write("revit/CLAUDE.md", self.document("revit-host", "The Revit host."))
        self.assertClean()

    def test_the_human_reading_surface_is_not_asked_for_frontmatter(self) -> None:
        """GitHub renders a frontmatter block as a visible table; these pages are read by people."""
        for relative in (
            "README.md",
            "CONTRIBUTING.md",
            "SECURITY.md",
            "ROADMAP.md",
            "CONTEXT.md",
            "spec/format.md",
            "revit/README.md",
        ):
            self.write(relative, "# A page a person reads\n")
        self.assertClean()

    def test_the_root_guide_itself_is_not_asked_for_frontmatter(self) -> None:
        self.assertClean()


class AdrIndexTests(GateTestCase):
    def test_an_adr_the_index_does_not_name_is_refused(self) -> None:
        self.write("docs/adr/0003-a-third-decision.md", self.document("adr-0003", "The third."))
        self.assertRefused("0003", "does not name it")

    def test_an_index_entry_with_no_file_behind_it_is_refused(self) -> None:
        (self.root / "docs/adr/0002-the-second-decision.md").unlink()
        self.assertRefused("names 0002", "no docs/adr/0002-*.md exists")

    def test_a_passing_mention_is_not_an_index_entry(self) -> None:
        """The real index says "0002's bug in the other host" in prose; bold is what lists."""
        self.assertClean()

    def test_a_tree_with_no_root_guide_is_refused_rather_than_passed_quietly(self) -> None:
        (self.root / "CLAUDE.md").unlink()
        self.assertRefused("the ADR index cannot be checked")

    def test_a_root_guide_with_no_index_bullet_is_refused(self) -> None:
        self.write("CLAUDE.md", "# Fixture guide\n\nNo index here.\n")
        self.assertRefused("no ADR index bullet found")


class UnitTests(unittest.TestCase):
    def test_blanked_code_keeps_line_numbers_true(self) -> None:
        text = "one\n```\ntwo\n```\n[x](y.md)\n"
        self.assertEqual(gate.link_targets(text), [(5, "y.md")])

    def test_an_image_target_is_a_target(self) -> None:
        self.assertEqual(gate.link_targets("![alt](docs/diagram.png)\n"), [(1, "docs/diagram.png")])

    def test_a_link_title_is_not_part_of_the_target(self) -> None:
        self.assertEqual(gate.link_targets('[x](y.md "A title")\n'), [(1, "y.md")])

    def test_angle_brackets_around_a_target_are_stripped(self) -> None:
        self.assertEqual(gate.link_targets("[x](<y.md>)\n"), [(1, "y.md")])

    def test_a_site_root_target_resolves_from_the_repository_root_to_name_the_intent(self) -> None:
        """It is refused either way; resolving it is what lets the finding say what was meant."""
        self.assertEqual(gate.resolved("docs/agents/x.md", "/spec/format.md"), "spec/format.md")

    def test_a_sibling_target_resolves_from_the_citing_file(self) -> None:
        self.assertEqual(gate.resolved("docs/agents/x.md", "domain.md"), "docs/agents/domain.md")


if __name__ == "__main__":
    unittest.main()
