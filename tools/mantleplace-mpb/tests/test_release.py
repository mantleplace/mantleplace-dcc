# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The package's version is the conformance suite's, and the release workflow publishes by trust.

The version `pip install mantleplace-mpb` reports says which MPB the validator and reader were
held to. These tests keep that true: a corpus case written for a newer manifest version fails
here until the package is raised with it, and a release tag that names another version is
refused before anything is published.
"""

from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

try:
    import tomllib
except ImportError:  # Python 3.10: the metadata test is skipped, the rest still runs
    tomllib = None  # type: ignore[assignment]

from helpers import write_corpus_index  # noqa: F401  (also puts src/ on sys.path)

PACKAGE = Path(__file__).resolve().parents[1]
REPO = PACKAGE.parents[1]
sys.path.insert(0, str(PACKAGE))

import release_check  # noqa: E402


class TheVersionIsTheSuites(unittest.TestCase):
    def test_the_package_version_is_the_newest_manifest_version_the_corpus_is_written_against(self) -> None:
        suite = release_check.suite_version(REPO / "tools" / "manifest-conformance" / "corpus" / "index.json")
        package = release_check.package_version(PACKAGE / "src" / "mantleplace_mpb" / "__init__.py")
        self.assertEqual(suite, release_check.base_version(package),
                         "raise __version__ with the suite: MAJOR.MINOR.PATCH is the MPB version "
                         "the corpus is written against, '.postN' marks a package-only release")

    def test_the_imported_version_is_the_one_the_release_reads(self) -> None:
        import mantleplace_mpb
        self.assertEqual(release_check.package_version(PACKAGE / "src" / "mantleplace_mpb" / "__init__.py"),
                         mantleplace_mpb.__version__)

    def test_the_suite_version_is_the_highest_semver_a_case_declares(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            index = write_corpus_index(Path(tmp), ["1.9.0", "1.14.0", "1.2.0", 19, "19", "1.0",
                                                   "1.0.0-rc1", "01.0.0", None])
            self.assertEqual("1.14.0", release_check.suite_version(index))

    def test_a_version_the_corpus_only_refuses_is_not_the_suites(self) -> None:
        """A reject case pins what a reader refuses: a 1.x reader given 2.0.0 refuses it, and a
        suite holding that case is still written against 1.x."""
        with tempfile.TemporaryDirectory() as tmp:
            index = write_corpus_index(Path(tmp), ["1.15.0"], rejects=["2.0.0", "1.999.0"])
            self.assertEqual("1.15.0", release_check.suite_version(index))

    def test_an_index_declaring_no_semver_has_no_suite_version(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            index = write_corpus_index(Path(tmp), [19, None])
            with self.assertRaises(ValueError):
                release_check.suite_version(index)


class TheReleaseCheck(unittest.TestCase):
    def test_the_version_forms_a_release_may_take(self) -> None:
        self.assertEqual("1.14.0", release_check.base_version("1.14.0"))
        self.assertEqual("1.14.0", release_check.base_version("1.14.0.post2"))
        for bad in ("1.14", "1.14.0rc1", "1.14.0.dev1", "v1.14.0", "1.14.0.post", "01.14.0"):
            with self.assertRaises(ValueError, msg=bad):
                release_check.base_version(bad)

    def test_a_tag_naming_the_package_version_passes(self) -> None:
        self.assertEqual([], release_check.problems("1.14.0", "1.14.0", tag="mpb-1.14.0"))
        self.assertEqual([], release_check.problems("1.14.0.post1", "1.14.0", tag="mpb-1.14.0.post1"))

    def test_a_tag_naming_another_version_is_a_problem(self) -> None:
        found = release_check.problems("1.14.0", "1.14.0", tag="mpb-1.13.0")
        self.assertEqual(1, len(found))
        self.assertIn("mpb-1.14.0", found[0])

    def test_a_tag_that_is_not_an_mpb_release_tag_is_a_problem(self) -> None:
        self.assertTrue(release_check.problems("1.14.0", "1.14.0", tag="v1.14.0"))

    def test_a_package_that_trails_the_suite_is_a_problem_with_or_without_a_tag(self) -> None:
        self.assertTrue(release_check.problems("1.13.0", "1.14.0"))
        self.assertTrue(release_check.problems("1.15.0.post1", "1.14.0", tag="mpb-1.15.0.post1"))

    def test_main_reports_a_mismatch_and_exits_one(self) -> None:
        self.assertEqual(0, release_check.main(["--tag", f"mpb-{release_check.package_version()}"]))
        self.assertEqual(1, release_check.main(["--tag", "mpb-0.0.1"]))


@unittest.skipIf(tomllib is None, "tomllib needs Python 3.11")
class ThePackageMetadata(unittest.TestCase):
    def setUp(self) -> None:
        self.project = tomllib.loads((PACKAGE / "pyproject.toml").read_text(encoding="utf-8"))["project"]

    def test_the_name_licence_and_python_floor_are_declared(self) -> None:
        self.assertEqual("mantleplace-mpb", self.project["name"])
        self.assertEqual("Apache-2.0", self.project["license"])
        self.assertEqual(">=3.10", self.project["requires-python"])
        self.assertEqual([], self.project["dependencies"])

    def test_the_version_has_one_source_the_package_itself(self) -> None:
        self.assertIn("version", self.project["dynamic"])
        self.assertNotIn("version", self.project)

    def test_a_reader_finds_what_it_is_and_where_it_lives(self) -> None:
        for key in ("description", "readme", "authors", "keywords", "classifiers", "urls"):
            self.assertTrue(self.project.get(key), key)
        self.assertEqual("mantleplace_mpb.cli:main", self.project["scripts"]["mpb"])
        for name in ("Homepage", "Repository", "Issues", "Changelog"):
            self.assertTrue(self.project["urls"][name].startswith("https://github.com/mantleplace/"), name)


class TheReleaseWorkflow(unittest.TestCase):
    def setUp(self) -> None:
        self.text = (REPO / ".github" / "workflows" / "release-mpb.yml").read_text(encoding="utf-8")

    def test_it_publishes_through_trusted_publishing_with_no_stored_token(self) -> None:
        self.assertIn("pypa/gh-action-pypi-publish@", self.text)
        self.assertIn("id-token: write", self.text)
        self.assertIn("environment:", self.text)
        for secret in ("password:", "PYPI_TOKEN", "PYPI_API_TOKEN", "secrets."):
            self.assertNotIn(secret, self.text, secret)

    def test_only_the_publish_job_may_mint_the_token(self) -> None:
        before, _, after = self.text.partition("\n  publish:")
        self.assertTrue(after, "a job named publish")
        self.assertNotIn("id-token", before)
        self.assertEqual(1, self.text.count("id-token: write"))

    def test_it_runs_on_a_release_tag_and_by_hand_never_on_a_pull_request(self) -> None:
        self.assertRegex(self.text, r"tags:\s*\n\s*- 'mpb-\*'")
        self.assertNotIn("pull_request", self.text)
        self.assertNotIn("self-hosted", self.text)

    def test_it_checks_the_tag_against_the_version_and_tests_before_it_builds(self) -> None:
        for step in ("release_check.py", "unittest discover", "python -m build", "twine check"):
            self.assertIn(step, self.text, step)
        self.assertLess(self.text.index("release_check.py"), self.text.index("python -m build"))
        self.assertLess(self.text.index("unittest discover"), self.text.index("python -m build"))

    def test_the_publish_job_installs_nothing_and_takes_the_built_files(self) -> None:
        _, _, publish = self.text.partition("\n  publish:")
        self.assertIn("download-artifact", publish)
        self.assertNotIn("pip install", publish)
        self.assertNotIn("actions/checkout", publish)

    def test_the_workflow_uses_spaces_not_tabs(self) -> None:
        self.assertNotIn(chr(9), self.text)


if __name__ == "__main__":
    unittest.main()
