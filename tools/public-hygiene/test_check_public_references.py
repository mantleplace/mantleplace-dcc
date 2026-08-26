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


def scan_text(text: str, label: str = "PR body") -> list[str]:
    return gate.violations_in_text(text, label, surface="metadata")


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


class RefusesInMetadata(unittest.TestCase):
    """Commit messages, PR titles and bodies — public the moment they are pushed, editable never
    (commits) or only with a public edit trail (PR text). The gate here is the last look anything
    gets before the reference is permanent."""

    def test_the_real_leak_that_reached_a_merged_commit(self):
        # Verbatim from the commit body that sat on main. The qualified form is not better than a
        # bare number: it hands a stranger the name of a private repository and a 404.
        found = scan_text("Refs mantleplace-nat#90.\n")
        self.assertEqual(len(found), 1)
        self.assertIn("mantleplace-nat#90", found[0])

    def test_an_owner_qualified_reference_to_another_repository(self):
        found = scan_text("carried over from acme/widgets#5\n")
        self.assertEqual(len(found), 1)

    def test_a_sibling_repository_never_named_in_the_gate_still_trips(self):
        # The pattern is structural. If this case needs a new literal in the gate's source to pass,
        # the gate has become the very list of private names it exists to keep out of this tree.
        found = scan_text("the fix lands in mantleplace-vault first\n")
        self.assertEqual(len(found), 1)

    def test_a_sibling_name_with_no_issue_number_is_still_a_citation(self):
        found = scan_text("the outcome goes into the mantleplace-nat first-import runbook\n")
        self.assertEqual(len(found), 1)

    def test_the_shorthand_forms_that_reached_a_pull_request_body(self):
        # "nat PR #68" and "nat #68" — the observed shapes. Lowercase and followed by a reference,
        # so prose about NAT traversal never trips (asserted on the other side).
        self.assertEqual(len(scan_text("Ported from nat PR #68, before the move.\n")), 1)
        self.assertEqual(len(scan_text("was produced on nat #68 against this code\n")), 1)

    def test_decision_log_id_is_refused_here_too(self):
        found = scan_text("Implements the decision recorded there as D-CX; the bump follows.\n")
        self.assertEqual(len(found), 1)
        self.assertIn("D-CX", found[0])

    def test_metadata_findings_carry_the_label_they_were_given(self):
        # "PR body:3:" is what lets a CI failure point at the field to edit rather than at a file
        # that does not exist.
        found = gate.violations_in_text("fine\nfine\nsee mantleplace-nat#1\n", "PR body",
                                        surface="metadata")
        self.assertTrue(found[0].startswith("PR body:3:"))


class AllowsInMetadata(unittest.TestCase):
    def test_a_bare_number_is_this_repositorys_own_voice(self):
        # The semantic split from the file surface, and the reason the two pattern sets must never
        # be merged: in a commit message or PR body, #12 is the native way to cite THIS repository's
        # own issue, and GitHub appends "(#NN)" to every squash-merge subject.
        self.assertEqual(scan_text("closes #12\n"), [])
        self.assertEqual(scan_text("fix(revit): build from the TIN (#27)\n"), [])

    def test_this_repository_may_cite_itself_by_full_name(self):
        self.assertEqual(scan_text("supersedes mantleplace-dcc#27\n"), [])
        self.assertEqual(scan_text("supersedes mantleplace/mantleplace-dcc#27\n"), [])

    def test_the_tracked_artifact_stems_that_forced_the_allowlist(self):
        # `.mantleplace-import.log` and `mantleplace-terrain*.log` are product filenames tracked in
        # this tree. A commit touching that code will name them; refusing that teaches people to
        # reword truthful messages, which is the beginning of the end of the gate.
        self.assertEqual(scan_text("stop rotating .mantleplace-import.log on failure\n"), [])
        self.assertEqual(scan_text("the probe writes mantleplace-terrain-probe.log beside it\n"), [])

    def test_networking_prose_is_not_a_repository(self):
        self.assertEqual(scan_text("NAT traversal is out of scope for the loopback server\n"), [])
        self.assertEqual(scan_text("gnat swarms are not a nat concern\n"), [])

    def test_a_code_span_is_how_this_rule_documents_itself(self):
        # The escape hatch that lets a PR body explain what the gate refuses without tripping it —
        # the same reasoning as the file surface's code-span exemption.
        self.assertEqual(scan_text("the gate refuses `mantleplace-nat#90` shapes\n"), [])

    def test_a_public_url_fragment_is_not_a_cross_repository_reference(self):
        self.assertEqual(scan_text("see https://mantle.place/spec#3 for the block\n"), [])


class BranchNames(unittest.TestCase):
    """A branch name is published by the push, listed by the API, and rendered forever in the header
    of any pull request it opens. It is the one surface with a shape rule as well as pattern rules:
    the convention is type/short-description, and an issue number is not a description."""

    def test_the_real_branch_that_encoded_a_private_issue(self):
        found = gate.branch_name_violations("fix/88-subdivision-drape-refusal")
        self.assertEqual(len(found), 1)

    def test_a_numeric_segment_anywhere_is_refused_not_just_first(self):
        self.assertEqual(len(gate.branch_name_violations("backport/88-drape/retry")), 1)
        self.assertEqual(len(gate.branch_name_violations("fix/88")), 1)

    def test_a_sibling_repository_name_in_a_branch_is_a_citation(self):
        self.assertEqual(len(gate.branch_name_violations("sync/mantleplace-vault-pin")), 1)

    def test_digits_inside_a_word_are_a_version_not_an_issue(self):
        # ue5-8, net10, mpb-1.0.0 — the digits that belong in branch names. The rule is anchored to
        # the start of a path segment because that is where an issue number lands.
        self.assertEqual(gate.branch_name_violations("feature/ue5-8-support"), [])
        self.assertEqual(gate.branch_name_violations("chore/net10-bump"), [])
        self.assertEqual(gate.branch_name_violations("feat/mpb-1.0.0-floor"), [])


class SurfacesStaySeparate(unittest.TestCase):
    """Each surface's pattern set is calibrated to what renders there. These two cases pin the
    calibration so a refactor cannot quietly merge the sets in either direction."""

    def test_the_file_surface_never_learns_the_sibling_pattern(self):
        # In a tracked file, `mantleplace-import.log` appears as plain prose far more often than a
        # sibling repo name ever could; the file surface's patterns stay as they are.
        self.assertEqual(scan("The importer writes .mantleplace-import.log beside the zip.\n"), [])

    def test_the_metadata_surface_never_learns_the_bare_number_pattern(self):
        self.assertEqual(scan_text("see #91 for the trace\n"), [])


if __name__ == "__main__":
    unittest.main()
