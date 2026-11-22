#!/usr/bin/env python3

from __future__ import annotations

import contextlib
import io
import pathlib
import tempfile
import unittest

import check_public_references as gate
import report_tracker_findings as reporter


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
        found = scan("closes #12\n")
        self.assertIn("private tracker", found[0])

    def test_the_message_does_not_over_claim_the_harm(self):
        found = scan("// see #12\n", "a.cs")
        self.assertIn("where GitHub renders Markdown", found[0])

    def test_only_the_corpus_is_self_exempt(self):
        self.assertEqual(
            gate.SELF_EXEMPT, frozenset({"tools/public-hygiene/test_check_public_references.py"})
        )

    def test_the_real_leak_that_prompted_this_gate(self):
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
        found = scan("The machine contract is the pointer values ([`format.md` \u00a73](format.md#3-the-pointer-doctrine)).\n")
        self.assertEqual(found, [])

    def test_a_link_target_does_not_exempt_the_rest_of_the_line(self):
        found = scan("see [the doctrine](format.md#3-the-pointer-doctrine) and also #91\n")
        self.assertEqual(len(found), 1)
        self.assertIn("#91", found[0])

    def test_code_span_is_how_the_rule_documents_itself(self):
        found = scan("A bare `#42` in a Markdown file auto-links to *this* repo's issue 42.\n")
        self.assertEqual(found, [])

    def test_host_ordinal_is_a_count_not_a_citation(self):
        self.assertEqual(scan("- **Role:** host #2, and the standard's debugger.\n"), [])
        self.assertEqual(scan("/// Host #2 does not reproduce that.\n", "TokenGrant.cs"), [])

    def test_css_hex_colour_is_not_an_issue_number(self):
        found = scan('html.Append("padding:0 1rem;color:#111}h1{font-size:1.25rem}");\n', "a.cs")
        self.assertEqual(found, [])

    def test_a_hex_colour_carrying_letters_is_not_an_issue_number(self):
        found = scan('html.Append("padding:2rem;background:#0B0C10;color:#fff;");\n', "a.cs")
        self.assertEqual(found, [])
        self.assertEqual(scan("body{background:#0b0c10}\n", "a.css"), [])

    def test_the_colour_exemption_still_needs_a_colour_shape(self):
        self.assertNotEqual(scan("fixes:#42 in the private tracker\n", "a.cs"), [])
        self.assertNotEqual(scan("fixes:#0B0C10FF is not a colour\n", "a.cs"), [])

    def test_public_rule_ids_are_prose_and_stay(self):
        found = scan("Registered in verified-against.json per HPS-38.\n")
        self.assertEqual(found, [])

    def test_a_lone_backtick_does_not_silence_the_rest_of_the_line(self):
        found = scan("an unmatched ` tick and then #99\n")
        self.assertEqual(len(found), 1)

    def test_an_exemption_elsewhere_on_the_line_does_not_launder_a_violation(self):
        found = scan("host #2 regressed this, see #91 for the trace\n")
        self.assertEqual(len(found), 1)
        self.assertIn("#91", found[0])

    def test_html_entity_is_not_an_issue_number(self):
        self.assertEqual(scan("&#39;\n", "a.cs"), [])


class RefusesInMetadata(unittest.TestCase):
    def test_the_real_leak_that_reached_a_merged_commit(self):
        found = scan_text("Refs mantleplace-foo#90.\n")
        self.assertEqual(len(found), 1)
        self.assertIn("mantleplace-foo#90", found[0])

    def test_an_owner_qualified_reference_to_another_repository(self):
        found = scan_text("carried over from acme/widgets#5\n")
        self.assertEqual(len(found), 1)

    def test_a_sibling_repository_never_named_in_the_gate_still_trips(self):
        found = scan_text("the fix lands in mantleplace-quux first\n")
        self.assertEqual(len(found), 1)

    def test_a_sibling_name_with_no_issue_number_is_still_a_citation(self):
        found = scan_text("the outcome goes into the mantleplace-foo first-import runbook\n")
        self.assertEqual(len(found), 1)

    def test_the_shorthand_forms_that_reached_a_pull_request_body(self):
        self.assertEqual(len(scan_text("Ported from nat PR #68, before the move.\n")), 1)
        self.assertEqual(len(scan_text("was produced on nat #68 against this code\n")), 1)

    def test_the_natural_retypings_of_the_observed_shapes_trip_too(self):
        self.assertEqual(len(scan_text("Ported from Nat PR #68, before the move.\n")), 1)
        self.assertEqual(len(scan_text("ported from nat issue #68 against this code\n")), 1)
        self.assertEqual(len(scan_text("the fix lands in Mantleplace-Quux first\n")), 1)

    def test_a_sibling_sharing_an_allowlisted_stem_still_trips(self):
        self.assertEqual(len(scan_text("mantleplace-import-api owns that half\n")), 1)
        self.assertEqual(len(scan_text("lands in mantleplace-terrain-service first\n")), 1)

    def test_decision_log_id_is_refused_here_too(self):
        found = scan_text("Implements the decision recorded there as D-CX; the bump follows.\n")
        self.assertEqual(len(found), 1)
        self.assertIn("D-CX", found[0])

    def test_metadata_findings_carry_the_label_they_were_given(self):
        found = gate.violations_in_text("fine\nfine\nsee mantleplace-foo#1\n", "PR body",
                                        surface="metadata")
        self.assertTrue(found[0].startswith("PR body:3:"))


class AllowsInMetadata(unittest.TestCase):
    def test_a_bare_number_is_this_repositorys_own_voice(self):
        self.assertEqual(scan_text("closes #12\n"), [])
        self.assertEqual(scan_text("fix(revit): build from the TIN (#27)\n"), [])

    def test_this_repository_may_cite_itself_by_full_name(self):
        self.assertEqual(scan_text("supersedes mantleplace-dcc#27\n"), [])
        self.assertEqual(scan_text("supersedes mantleplace/mantleplace-dcc#27\n"), [])

    def test_the_tracked_artifact_stems_that_forced_the_allowlist(self):
        self.assertEqual(scan_text("stop rotating .mantleplace-import.log on failure\n"), [])
        self.assertEqual(scan_text("the probe writes mantleplace-terrain-probe.log beside it\n"), [])

    def test_networking_prose_is_not_a_repository(self):
        self.assertEqual(scan_text("NAT traversal is out of scope for the loopback server\n"), [])
        self.assertEqual(scan_text("gnat swarms are not a nat concern\n"), [])

    def test_a_code_span_is_how_this_rule_documents_itself(self):
        self.assertEqual(scan_text("the gate refuses `mantleplace-foo#90` shapes\n"), [])

    def test_a_public_url_fragment_is_not_a_cross_repository_reference(self):
        self.assertEqual(scan_text("see https://mantle.place/spec#3 for the block\n"), [])

    def test_a_language_version_is_not_a_repository(self):
        self.assertEqual(scan_text("revit: adopt C#12 collection expressions\n"), [])
        self.assertEqual(scan_text("the F#7 sample was removed\n"), [])


class BranchNames(unittest.TestCase):
    def test_the_real_branch_that_encoded_a_private_issue(self):
        found = gate.branch_name_violations("fix/88-subdivision-drape-refusal")
        self.assertEqual(len(found), 1)

    def test_a_numeric_segment_anywhere_is_refused_not_just_first(self):
        self.assertEqual(len(gate.branch_name_violations("backport/88-drape/retry")), 1)
        self.assertEqual(len(gate.branch_name_violations("fix/88")), 1)

    def test_dressing_the_number_up_does_not_pass_it(self):
        self.assertEqual(len(gate.branch_name_violations("fix/#88-subdivision-drape")), 1)
        self.assertEqual(len(gate.branch_name_violations("fix/issue-88-subdivision-drape")), 1)
        self.assertEqual(len(gate.branch_name_violations("fix/gh-88-subdivision-drape")), 1)

    def test_a_sibling_repository_name_in_a_branch_is_a_citation(self):
        self.assertEqual(len(gate.branch_name_violations("sync/mantleplace-quux-pin")), 1)

    def test_digits_inside_a_word_are_a_version_not_an_issue(self):
        self.assertEqual(gate.branch_name_violations("feature/ue5-8-support"), [])
        self.assertEqual(gate.branch_name_violations("chore/net10-bump"), [])
        self.assertEqual(gate.branch_name_violations("feat/mpb-1.0.0-floor"), [])


class CommitMessageFiles(unittest.TestCase):
    def test_comment_lines_and_the_scissors_diff_are_not_the_message(self):
        editmsg = (
            "fix: tidy the gate's own corpus\n"
            "\n"
            "# Please enter the commit message for your changes.\n"
            "# On branch chore/x — mantleplace-quux-notes.txt untracked\n"
            "# ------------------------ >8 ------------------------\n"
            "diff --git a/t.py b/t.py\n"
            '+        scan_text("the fix lands in mantleplace-quux first\\n")\n'
        )
        self.assertEqual(gate.violations_in_text(
            gate.commit_message_body(editmsg), "commit message", surface="metadata"), [])

    def test_what_survives_cleanup_is_still_judged(self):
        editmsg = "Refs mantleplace-foo#90.\n# a comment\n"
        found = gate.violations_in_text(
            gate.commit_message_body(editmsg), "commit message", surface="metadata")
        self.assertEqual(len(found), 1)

    def test_an_undecodable_message_file_reports_rather_than_crashes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "COMMIT_EDITMSG"
            path.write_bytes("caf\xe9 fix\n".encode("cp1252"))
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(gate.main(["--commit-msg-file", str(path)]), 0)


class ModesFailLoudly(unittest.TestCase):
    def test_an_empty_branch_name_is_an_error_not_a_file_scan(self):
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as caught:
                gate.main(["--branch-name", ""])
        self.assertNotEqual(caught.exception.code, 0)

    def test_a_bare_commit_range_is_an_error_not_a_file_scan(self):
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as caught:
                gate.main(["--commit-range"])
        self.assertNotEqual(caught.exception.code, 0)


class SurfacesStaySeparate(unittest.TestCase):
    def test_the_file_surface_learned_the_sibling_pattern_but_keeps_the_artifact_filenames(self):
        self.assertEqual(scan("The importer writes .mantleplace-import.log beside the zip.\n"), [])

    def test_the_metadata_surface_never_learns_the_bare_number_pattern(self):
        self.assertEqual(scan_text("see #91 for the trace\n"), [])


class SiblingNamesInTrackedFiles(unittest.TestCase):
    def test_a_sibling_name_in_a_markdown_file_is_refused(self):
        found = scan("The fix lands in mantleplace-widgets first.\n")
        self.assertEqual(len(found), 1)
        self.assertIn("sample.md:1", found[0])
        self.assertIn("mantleplace-widgets", found[0])

    def test_a_sibling_name_in_source_is_refused(self):
        self.assertEqual(len(scan('// owned by mantleplace-widgets\n', "a.cs")), 1)
        self.assertEqual(len(scan("# synced from mantleplace-widgets\n", "a.py")), 1)
        self.assertEqual(len(scan("uses: mantleplace/mantleplace-widgets/.x@v1\n", "a.yml")), 1)

    def test_the_natural_retypings_trip_too(self):
        self.assertEqual(len(scan("Mantleplace-Widgets owns that half.\n")), 1)
        self.assertEqual(len(scan("MANTLEPLACE-WIDGETS owns that half.\n")), 1)

    def test_a_sibling_sharing_an_allowlisted_stem_still_trips(self):
        self.assertEqual(len(scan("mantleplace-import-api owns that half\n")), 1)
        self.assertEqual(len(scan("lands in mantleplace-terrain-service first\n")), 1)

    def test_the_two_tracked_artifact_filenames_pass(self):
        self.assertEqual(scan("The importer writes .mantleplace-import.log beside the zip.\n"), [])
        self.assertEqual(scan("The probe writes mantleplace-terrain-probe.log beside it.\n"), [])

    def test_a_capitalised_or_upper_case_sibling_tail_is_refused_in_a_file(self):
        for name in ("MantlePlace-Foo", "MantlePlace-QUUX", "MantlePlace-Example1", "MantlePlace-Zzz-Bundle",
                     "Mantleplace-Foo", "MANTLEPLACE-TERMS", "mantleplace-Terms", "MantlePlace-Termsfoo"):
            with self.subTest(name=name):
                self.assertEqual(len(scan(f"The fix lands in {name} first.\n")), 1)

    def test_every_tracked_brand_string_still_passes(self):
        for tail in sorted(gate.BRAND_TAILS):
            with self.subTest(tail=tail):
                self.assertEqual(scan(f'x = "MantlePlace-{tail}";\n', "a.cs"), [])
                self.assertEqual(scan(f"ships MantlePlace-{tail}-x.zip\n"), [])
        self.assertEqual(
            gate.BRAND_TAILS,
            frozenset({"Terms", "Surface", "Revit", "UE5", "AutoCad", "Linework", "Contours",
                       "Aerial", "Records", "Sample"}),
        )

    def test_brand_strings_are_not_an_allowance_on_the_metadata_surface(self):
        self.assertEqual(len(scan_text("see MantlePlace-Terms\n")), 1)

    def test_this_repository_by_name_passes(self):
        self.assertEqual(scan("clone https://github.com/mantleplace/mantleplace-dcc.git\n"), [])

    def test_a_code_span_is_how_the_rule_documents_itself(self):
        self.assertEqual(scan("The gate refuses `mantleplace-widgets` shapes.\n"), [])

    def test_the_product_brand_casing_is_a_string_the_plugins_write_not_a_repository(self):
        self.assertEqual(scan('layer = "MantlePlace-Surface-x";\n', "a.cs"), [])
        self.assertEqual(scan("ships MantlePlace-Revit-1.2.0.zip\n"), [])

    def test_the_tracked_identifiers_in_code_pass_only_as_exact_tokens(self):
        self.assertEqual(scan("pip install mantleplace-mpb\n"), [])
        self.assertEqual(scan('var id = "mantleplace-autocad-bundle";\n', "a.cs"), [])
        self.assertEqual(len(scan("pip install mantleplace-mpb-secrets\n")), 1)

    def test_a_finding_in_a_file_redacts_for_a_published_report(self):
        found = gate.violations_in_text(
            "see mantleplace-widgets\n", "a.md", surface="file", redact=True
        )
        self.assertEqual(len(found), 1)
        self.assertNotIn("mantleplace-widgets", found[0])

    def test_the_gates_own_script_is_held_to_the_rule(self):
        root = pathlib.Path(gate.__file__).resolve().parents[2]
        script = pathlib.Path(gate.__file__)
        relative = script.resolve().relative_to(root).as_posix()
        self.assertEqual(
            [line for line in gate.violations(script, relative) if "sibling" in line], []
        )


class AgentToolingIsRefusedOnEverySurface(unittest.TestCase):
    INSTALL = "/plugin install widgets@mantleplace"
    INSTALL_TAILED = "claude plugin install mantleplace-widgets@mantleplace-internal"
    SETTING = '  "enabledPlugins": { "widgets@mantleplace": true }'
    COMMAND = "run /mantleplace-widgets:close when the branch is green"

    def surfaces(self, text: str):
        return {
            "markdown": scan(text + "\n"),
            "source": scan(text + "\n", "a.py"),
            "json": scan(text + "\n", "settings.json"),
            "pr body": scan_text(text + "\n"),
            "commit message": gate.violations_in_text(text + "\n", "commit abc", surface="metadata"),
            "branch": gate.branch_name_violations(text.replace(" ", "-")),
        }

    def assert_refused_everywhere(self, text: str, fragment: str):
        for surface, found in self.surfaces(text).items():
            with self.subTest(surface=surface, text=text):
                self.assertTrue(found, f"{surface} let {text!r} through")
                self.assertTrue(any(fragment in line for line in found), found)

    def test_a_plugin_install_reference_is_refused(self):
        self.assert_refused_everywhere(self.INSTALL, "widgets@mantleplace")
        self.assert_refused_everywhere(self.INSTALL_TAILED, "@mantleplace-internal")

    def test_a_plugin_enabling_setting_is_refused(self):
        self.assert_refused_everywhere(self.SETTING, "widgets@mantleplace")

    def test_a_namespaced_slash_command_is_refused(self):
        self.assert_refused_everywhere(self.COMMAND, "/mantleplace-widgets:close")
        self.assert_refused_everywhere("then /mantleplace:close", "/mantleplace:close")

    def test_each_reference_is_one_finding_not_two(self):
        self.assertEqual(len(scan(self.INSTALL + "\n", "a.py")), 1)
        self.assertEqual(len(scan_text(self.COMMAND + "\n")), 1)

    def test_a_code_span_does_not_exempt_tooling(self):
        self.assertEqual(len(scan(f"Run `{self.INSTALL}` first.\n")), 1)
        self.assertEqual(len(scan_text(f"Run `{self.COMMAND}`.\n")), 1)

    def test_a_multiline_settings_block_is_caught_on_the_entry_line(self):
        block = '{\n  "enabledPlugins": {\n    "widgets@mantleplace": true\n  }\n}\n'
        found = scan(block, "settings.json")
        self.assertEqual(len(found), 1)
        self.assertIn("settings.json:3", found[0])

    def test_findings_redact_for_a_published_report(self):
        for text in (self.INSTALL, self.SETTING, self.COMMAND):
            with self.subTest(text=text):
                found = gate.violations_in_text(text + "\n", "issue body", surface="metadata",
                                                redact=True)
                self.assertEqual(len(found), 1)
                self.assertNotIn("widgets", found[0])

    def test_no_pattern_the_gate_carries_names_a_private_thing(self):
        for pattern in (gate.PRIVATE_SIBLING, gate.PLUGIN_AT_CATALOGUE, gate.NAMESPACED_COMMAND):
            for private in ("foo", "quux", "zzz", "example"):
                self.assertNotIn(private, pattern.pattern)

    def test_a_namespaced_name_without_the_slash_is_refused(self):
        for text in ("`mantleplace-widgets:close`", "run mantleplace-foo-bar:go now"):
            with self.subTest(text=text):
                self.assertEqual(len(scan(text + "\n")), 1)
                self.assertEqual(len(scan_text(text + "\n")), 1)

    def test_this_repositorys_own_name_with_a_tag_is_not_a_namespaced_name(self):
        self.assertEqual(scan("docker run mantleplace-dcc:latest\n"), [])

    def test_an_email_address_at_the_stem_domain_is_not_a_catalogue(self):
        self.assertEqual(scan("write to hello@mantleplace.com or me@mantleplace.example\n"), [])
        self.assertEqual(scan_text("Signed-off-by: A <a@mantleplace.com>\n"), [])

    def test_an_npm_scope_or_a_package_at_a_version_is_not_a_catalogue(self):
        self.assertEqual(scan("npm i @types/node@20 and pip install foo@1.2\n"), [])

    def test_a_url_with_a_port_is_not_a_slash_command(self):
        self.assertEqual(scan("open http://mantleplace:8080/x and http://mantleplace:abc/y\n"), [])

    def test_an_ordinary_slash_command_is_not_ours(self):
        self.assertEqual(scan("run /plugin install and /help and /compact\n"), [])


class TrackerTextIsTheSixthSurface(unittest.TestCase):
    def test_a_bare_self_citation_in_an_issue_body_stays_legal(self):
        self.assertEqual(scan_text("Superseded by #76; take the masks there.\n", "issue #93 body"), [])

    def test_the_qualified_form_is_refused_in_an_issue_body(self):
        found = scan_text("Blocked on mantleplace-foo#113.\n", "issue #94 body")
        self.assertEqual(len(found), 1)
        self.assertIn("issue #94 body", found[0])

    def test_a_private_sibling_name_is_refused_in_a_comment(self):
        found = scan_text("The numbers live in mantleplace-foo.\n", "comment 5051 on issue #94")
        self.assertEqual(len(found), 1)

    def test_a_decision_log_id_is_refused_in_an_issue_title(self):
        found = scan_text("Fold D-CX into the ignore block", "issue #94 title")
        self.assertEqual(len(found), 1)


class RedactedFindingsDoNotRepublish(unittest.TestCase):
    def test_the_refused_token_is_not_echoed(self):
        found = gate.violations_in_text(
            "Blocked on mantleplace-foo#113.\n", "issue #94 body", surface="metadata", redact=True
        )
        self.assertEqual(len(found), 1)
        self.assertNotIn("mantleplace-foo", found[0])
        self.assertNotIn("#113", found[0])

    def test_a_redacted_finding_still_says_where_and_what_rule(self):
        found = gate.violations_in_text(
            "Blocked on mantleplace-foo#113.\n", "issue #94 body", surface="metadata", redact=True
        )
        self.assertIn("issue #94 body:1", found[0])
        self.assertIn("another repository", found[0])

    def test_every_refusal_shape_redacts(self):
        cases = [
            "Blocked on mantleplace-foo#113.\n",
            "The numbers live in mantleplace-foo.\n",
            "Recorded there as D-CX.\n",
            "Ported from nat #68 against this code\n",
        ]
        for text in cases:
            with self.subTest(text=text):
                found = gate.violations_in_text(text, "issue body", surface="metadata", redact=True)
                self.assertEqual(len(found), 1)
                self.assertNotIn("mantleplace-foo", found[0])
                self.assertNotIn("D-CX", found[0])
                self.assertNotIn("#68", found[0])

    def test_the_file_surface_redacts_too(self):
        found = gate.violations_in_text("see #91 for the trace\n", "a.md", redact=True)
        self.assertEqual(len(found), 1)
        self.assertNotIn("#91", found[0])
        self.assertIn("private tracker", found[0])

    def test_unredacted_is_still_the_default(self):
        found = scan_text("Blocked on mantleplace-foo#113.\n")
        self.assertIn("mantleplace-foo#113", found[0])

    def test_the_branch_shape_rule_redacts_its_name(self):
        found = gate.branch_name_violations("88-fix-the-drape", redact=True)
        self.assertTrue(found)
        self.assertTrue(all("88-fix-the-drape" not in line for line in found))


class TheTrackerReportSaysWhatItIs(unittest.TestCase):
    def test_the_posted_body_carries_no_refused_token(self):
        findings = gate.violations_in_text(
            "Blocked on mantleplace-foo#113, recorded as D-CX.\n",
            "issue 94 body",
            surface="metadata",
            redact=True,
        )
        body = reporter.FOUND_BODY.format(
            marker=reporter.MARKER,
            findings="\n".join(findings),
            run_url="https://example.invalid/run/1",
            footer=reporter.FOOTER,
        )
        self.assertNotIn("mantleplace-foo", body)
        self.assertNotIn("#113", body)
        self.assertNotIn("D-CX", body)

    def test_the_posted_body_names_the_run_that_has_the_detail(self):
        body = reporter.FOUND_BODY.format(
            marker=reporter.MARKER,
            findings="  issue 94 body:1: a refused token cites an issue in another repository",
            run_url="https://example.invalid/run/1",
            footer=reporter.FOOTER,
        )
        self.assertIn("https://example.invalid/run/1", body)

    def test_both_bodies_say_detection_rather_than_prevention(self):
        self.assertIn("detection, not prevention", reporter.FOOTER)
        self.assertIn(reporter.FOOTER, reporter.CLEAN_BODY)
        self.assertIn("{footer}", reporter.FOUND_BODY)

    def test_the_marker_is_invisible_to_a_reader_and_exact_to_match(self):
        self.assertTrue(reporter.MARKER.startswith("<!--"))
        self.assertTrue(reporter.MARKER.endswith("-->"))
        self.assertIn(reporter.MARKER, reporter.CLEAN_BODY)

    def test_a_comment_url_resolves_to_its_id(self):
        url = "https://github.com/o/r/issues/94#issuecomment-3344556677"
        self.assertEqual(url.rsplit("issuecomment-", 1)[-1], "3344556677")


class RuleIdsMustResolve(unittest.TestCase):
    def test_a_rule_statement_is_a_definition(self):
        published = "⛔ `HPS-01` — Every host plugin implements four layers.\n"
        self.assertEqual(gate.defined_rule_ids(published), {"HPS-01"})

    def test_every_shape_the_document_actually_uses(self):
        published = (
            "⛔ `HPS-01` — hard rule, unbolded\n"
            "`HPS-02` — plain rule\n"
            "- ⛔ **`HPS-46a` — a bolded rule inside a list**\n"
            "  **`HPS-46b` — indented and bolded**\n"
        )
        self.assertEqual(
            gate.defined_rule_ids(published), {"HPS-01", "HPS-02", "HPS-46a", "HPS-46b"}
        )

    def test_a_passing_MENTION_is_not_a_definition(self):
        published = "Resolved on `HPS-46a`'s precedent, and see `HPS-99` for the rest.\n"
        self.assertEqual(gate.defined_rule_ids(published), set())

    def test_a_cited_id_that_does_not_resolve_is_refused(self):
        found = gate.unresolved_rule_ids(
            "The refresh token is stored per-OS-user (`HPS-14`), verified before rename (`HPS-99`).\n",
            "revit/README.md",
            {"HPS-14"},
        )
        self.assertEqual(len(found), 1)
        self.assertIn("HPS-99", found[0])
        self.assertIn("revit/README.md", found[0])

    def test_the_message_says_where_the_rule_should_live(self):
        found = gate.unresolved_rule_ids("see `HPS-99`\n", "a.md", {"HPS-14"})
        self.assertIn(gate.PUBLISHED_STANDARD, found[0])

    def test_a_resolving_id_passes_on_every_surface_a_rule_is_cited_from(self):
        for text in (
            "// HPS-33: applied verbatim, never re-derived\n",
            "The manifest's values are applied verbatim (`HPS-33`).\n",
            '{"rule": "HPS-33"}\n',
        ):
            with self.subTest(text=text):
                self.assertEqual(gate.unresolved_rule_ids(text, "a.cs", {"HPS-33"}), [])

    def test_the_lettered_variants_are_distinct_ids(self):
        found = gate.unresolved_rule_ids("see `HPS-46`\n", "a.md", {"HPS-46a"})
        self.assertEqual(len(found), 1)

    def test_the_published_standard_is_not_checked_against_itself(self):
        self.assertIn(gate.PUBLISHED_STANDARD, gate.RULE_SELF_EXEMPT)

    def test_several_unresolved_on_one_line_are_all_reported(self):
        found = gate.unresolved_rule_ids("see `HPS-98` and `HPS-99`\n", "a.md", set())
        self.assertEqual(len(found), 2)


if __name__ == "__main__":
    unittest.main()
