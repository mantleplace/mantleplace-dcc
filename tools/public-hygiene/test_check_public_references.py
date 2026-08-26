#!/usr/bin/env python3
"""Offline cases for the private-reference gate.

The gate's own failure modes are asymmetric, so both directions are asserted. A gate that misses a
violation lets a misleading link reach a stranger; a gate that flags a Markdown anchor or a hex
colour gets switched off within a week, and then it misses everything.
"""

from __future__ import annotations

import pathlib
import tempfile
import unittest

import check_public_references as gate


def scan(text: str, name: str = "sample.md") -> list[str]:
    with tempfile.TemporaryDirectory() as directory:
        path = pathlib.Path(directory) / name
        path.write_text(text, encoding="utf-8")
        return gate.violations(path, name)


class RefusesPrivateReferences(unittest.TestCase):
    def test_bare_issue_number_is_refused(self):
        found = scan("This was fixed in #88, see there for the trace.\n")
        self.assertEqual(len(found), 1)
        self.assertIn("#88", found[0])

    def test_the_message_says_why_rather_than_just_what(self):
        # A gate that prints "violation on line 3" teaches nobody. The reason is what stops the
        # next occurrence, since the author's mental model is what was wrong.
        found = scan("closes #12\n")
        self.assertIn("private tracker", found[0])

    def test_the_message_does_not_over_claim_the_harm(self):
        # An earlier cut said flatly that the reference "auto-links". In a .cs file it does not —
        # GitHub only links where it renders Markdown. A gate caught overstating its own case is a
        # gate whose next true finding gets argued with.
        found = scan("// see #12\n", "a.cs")
        self.assertIn("where GitHub renders Markdown", found[0])

    def test_only_the_corpus_is_self_exempt(self):
        # The exemption must not widen to the directory: this gate's own script carries ordinary
        # prose and is held to the rule like anything else.
        self.assertEqual(
            gate.SELF_EXEMPT, frozenset({"tools/public-hygiene/test_check_public_references.py"})
        )

    def test_the_real_leak_that_prompted_this_gate(self):
        # Verbatim from the comment that reached main, where it rendered as a link to an unrelated
        # public issue.
        found = scan(
            "            // #89's second correction: the drape's ChangeTypeId costs nearly as much\n",
            "SlowStepNoticeTests.cs",
        )
        self.assertEqual(len(found), 1)

    def test_decision_log_id_is_refused(self):
        found = scan("The same line D-BF drew when it declined to edit the type.\n")
        self.assertEqual(len(found), 1)
        self.assertIn("D-BF", found[0])

    def test_several_on_one_line_are_all_reported(self):
        # Reporting only the first would turn one fix into several rounds of the same gate.
        self.assertEqual(len(scan("see #12 and #34\n")), 2)

    def test_every_scanned_suffix_is_scanned_the_same(self):
        for name in ("a.cs", "b.md", "c.py", "d.yml", "e.json", "f.ps1"):
            with self.subTest(name=name):
                self.assertEqual(len(scan("ref #7\n", name)), 1)


class AllowsWhatIsLegitimate(unittest.TestCase):
    def test_markdown_anchor_is_not_a_tracker(self):
        found = scan("plus one extra block — see [§7](#7-the-sidecar-manifest). Inside the zip,\n")
        self.assertEqual(found, [])

    def test_cross_file_section_link_is_not_a_tracker(self):
        # The form the first cut of this gate missed. It resolves for every reader: it points at a
        # heading in another file of this same public repository.
        found = scan("The machine contract is the pointer values ([`format.md` \u00a73](format.md#3-the-pointer-doctrine)).\n")
        self.assertEqual(found, [])

    def test_a_link_target_does_not_exempt_the_rest_of_the_line(self):
        found = scan("see [the doctrine](format.md#3-the-pointer-doctrine) and also #91\n")
        self.assertEqual(len(found), 1)
        self.assertIn("#91", found[0])

    def test_code_span_is_how_the_rule_documents_itself(self):
        # CLAUDE.md states the rule using an example. A gate that fails its own rulebook is a gate
        # somebody deletes.
        found = scan("A bare `#42` in a Markdown file auto-links to *this* repo's issue 42.\n")
        self.assertEqual(found, [])

    def test_host_ordinal_is_a_count_not_a_citation(self):
        self.assertEqual(scan("- **Role:** host #2, and the standard's debugger.\n"), [])
        self.assertEqual(scan("/// Host #2 does not reproduce that.\n", "TokenGrant.cs"), [])

    def test_css_hex_colour_is_not_an_issue_number(self):
        found = scan('html.Append("padding:0 1rem;color:#111}h1{font-size:1.25rem}");\n', "a.cs")
        self.assertEqual(found, [])

    def test_public_rule_ids_are_prose_and_stay(self):
        # The distinction CLAUDE.md already draws: rule ids are stable public identifiers.
        found = scan("Registered in verified-against.json per HPS-38, and DOC-06 places it.\n")
        self.assertEqual(found, [])

    def test_a_lone_backtick_does_not_silence_the_rest_of_the_line(self):
        # Otherwise one stray backtick anywhere becomes a universal waiver.
        found = scan("an unmatched ` tick and then #99\n")
        self.assertEqual(len(found), 1)

    def test_an_exemption_elsewhere_on_the_line_does_not_launder_a_violation(self):
        # "host #2" earlier in the line must not exempt a real citation later in it.
        found = scan("host #2 regressed this, see #91 for the trace\n")
        self.assertEqual(len(found), 1)
        self.assertIn("#91", found[0])

    def test_html_entity_is_not_an_issue_number(self):
        self.assertEqual(scan("&#39;\n", "a.cs"), [])


if __name__ == "__main__":
    unittest.main()
