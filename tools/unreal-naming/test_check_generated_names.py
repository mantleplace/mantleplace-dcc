#!/usr/bin/env python3
"""Tests for the generated-name drift gate.

Each test builds a throwaway tree rather than asserting against the real one, so the suite says what
the gate MEANS rather than what the tree happens to contain today. The false-positive cases matter as
much as the true ones: a gate that fires on a legitimate line gets silenced wholesale, and then it
guards nothing.
"""

from __future__ import annotations

import io
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from tempfile import TemporaryDirectory

import check_generated_names as gate

SOURCE_DIR = Path("unreal/MantlePlace/Source/MantlePlaceEditor/Private")


class GateTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

    def write(self, relative: str, body: str) -> Path:
        path = self.root / SOURCE_DIR / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(body, encoding="utf-8")
        return path

    def run_gate(self) -> tuple[int, str]:
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            code = gate.run(self.root)
        return code, buffer.getvalue()


class RefusedTests(GateTestCase):
    def test_content_path_literal_is_refused(self) -> None:
        self.write("Importer.cpp", 'const FString P = TEXT("/Game/MantlePlace/abc");\n')
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("[content-path]", out)
        self.assertIn("Importer.cpp:1", out)

    def test_actor_label_literal_is_refused(self) -> None:
        self.write("Importer.cpp", 'A->SetActorLabel(TEXT("MP_Landscape_abc"));\n')
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("[actor-label]", out)

    def test_each_asset_prefix_is_refused(self) -> None:
        for prefix in ("SM_", "T_", "M_", "MI_", "LI_", "DT_", "BP_"):
            with self.subTest(prefix=prefix):
                self.write("Importer.cpp", f'Name = TEXT("{prefix}Thing");\n')
                code, out = self.run_gate()
                self.assertEqual(code, 1, f"{prefix} should be refused")
                self.assertIn("[asset-prefix]", out)

    def test_subfolder_as_path_segment_is_refused(self) -> None:
        for subfolder in gate.SUBFOLDERS:
            with self.subTest(subfolder=subfolder):
                self.write("Importer.cpp", f'P = Dest / TEXT("{subfolder}");\n')
                code, out = self.run_gate()
                self.assertEqual(code, 1, f"{subfolder} should be refused")
                self.assertIn("[subfolder]", out)

    def test_an_outliner_folder_literal_is_refused(self) -> None:
        self.write("Importer.cpp", 'A->SetFolderPath(FName(TEXT("MantlePlace/abc")));\n')
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("[outliner-folder]", out)

    def test_the_import_tag_literal_is_refused(self) -> None:
        self.write("Importer.cpp", 'A->Tags.Add(FName(TEXT("mantleplace_import=abc")));\n')
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("[import-tag]", out)

    def test_identity_truncation_is_refused(self) -> None:
        for expression in ("Manifest.JobId.Left(8)", "Manifest.OrderId.Left(8)", "Identity.Left(12)"):
            with self.subTest(expression=expression):
                self.write("Importer.cpp", f"const FString S = {expression};\n")
                code, out = self.run_gate()
                self.assertEqual(code, 1, f"{expression} should be refused")
                self.assertIn("[identity-truncation]", out)

    def test_every_problem_is_reported_not_just_the_first(self) -> None:
        self.write(
            "Importer.cpp",
            'A = TEXT("/Game/X");\n'
            'B = TEXT("MP_Y");\n'
            'C = Dest / TEXT("Imagery");\n',
        )
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("[content-path]", out)
        self.assertIn("[actor-label]", out)
        self.assertIn("[subfolder]", out)
        self.assertIn("3 problem(s)", out)

    def test_the_offending_line_is_quoted(self) -> None:
        self.write("Importer.cpp", '    const FString P = TEXT("/Game/Nope");\n')
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn('const FString P = TEXT("/Game/Nope");', out)


class AllowedTests(GateTestCase):
    def test_a_clean_tree_passes(self) -> None:
        self.write(
            "Importer.cpp",
            "const FString P = MantlePlaceImportNaming::ImportRoot(\n"
            "    MantlePlaceImportNaming::DefaultContentRoot(), Manifest.JobId);\n",
        )
        code, out = self.run_gate()
        self.assertEqual(code, 0)
        self.assertIn("OK:", out)

    def test_the_naming_module_may_build_them(self) -> None:
        for name in gate.NAMING_MODULE:
            with self.subTest(name=name):
                self.write(name, 'return TEXT("/Game/MantlePlace");\n')
                code, _ = self.run_gate()
                self.assertEqual(code, 0, f"{name} defines these strings")

    def test_tests_may_assert_them(self) -> None:
        path = self.root / SOURCE_DIR / "Tests" / "NamingTest.cpp"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('TestEqual(TEXT("x"), R, TEXT("/Game/MantlePlace/abc"));\n', encoding="utf-8")
        code, _ = self.run_gate()
        self.assertEqual(code, 0)

    def test_the_plugin_mount_path_is_not_a_game_path(self) -> None:
        """The drape template is shipped content, addressed through the plugin's own mount."""
        self.write(
            "Drape.cpp",
            'static const TCHAR* const T = TEXT("/MantlePlace/Material/M_MantlePlace_Drape");\n',
        )
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_a_subfolder_word_outside_a_path_is_left_alone(self) -> None:
        """The vault panel offers "Mesh" as an import mode. That is a UI string, not a path."""
        self.write("Panel.cpp", 'Options.Add(MakeShared<FString>(TEXT("Mesh")));\n')
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_a_paint_layer_name_read_from_the_manifest_is_left_alone(self) -> None:
        """HPS-33: the layer name is applied verbatim. Reading one is not naming one."""
        self.write("Landscape.cpp", "LayerInfo->SetLayerName(FName(*Material), false);\n")
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_an_on_disk_folder_of_the_same_name_is_left_alone(self) -> None:
        """The bundle cache lives at MantlePlace/VaultCache on disk. Not an outliner folder."""
        self.write("Cache.cpp", 'const FString Sub = TEXT("MantlePlace/VaultCache");\n')
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_a_folder_path_from_the_naming_module_is_left_alone(self) -> None:
        self.write(
            "Importer.cpp",
            "A->SetFolderPath(FName(*MantlePlaceImportNaming::OutlinerFolder(Identity)));\n",
        )
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_an_unrelated_left_call_is_left_alone(self) -> None:
        self.write("Cache.cpp", "return Root / (Sanitized + Hash.Left(8));\n")
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_an_allowance_with_a_reason_silences_one_line(self) -> None:
        self.write(
            "Importer.cpp",
            'P = TEXT("/Game/Legacy");  // naming-gate: allow reads a pre-0.4.0 layout\n',
        )
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_an_allowance_without_a_reason_is_itself_refused(self) -> None:
        self.write("Importer.cpp", 'P = TEXT("/Game/X");  // naming-gate: allow\n')
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("needs a reason", out)

    def test_an_allowance_does_not_silence_the_next_line(self) -> None:
        self.write(
            "Importer.cpp",
            'A = TEXT("/Game/One");  // naming-gate: allow deliberate\n'
            'B = TEXT("/Game/Two");\n',
        )
        code, out = self.run_gate()
        self.assertEqual(code, 1)
        self.assertIn("Importer.cpp:2", out)
        self.assertNotIn("Importer.cpp:1", out)


class ScopeTests(GateTestCase):
    def test_only_cpp_and_h_are_scanned(self) -> None:
        self.write("notes.txt", 'TEXT("/Game/Whatever")\n')
        self.write("script.py", 'TEXT("/Game/Whatever")\n')
        code, out = self.run_gate()
        self.assertEqual(code, 0, out)

    def test_a_missing_source_tree_is_not_a_pass_by_accident(self) -> None:
        """No files scanned should be visible in the output, not silently reported as OK."""
        code, out = self.run_gate()
        self.assertEqual(code, 0)
        self.assertIn("0 files", out)

    def test_a_bom_encoded_file_is_read_not_mangled(self) -> None:
        path = self.root / SOURCE_DIR / "Bom.cpp"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('P = TEXT("/Game/X");\n', encoding="utf-8-sig")
        code, out = self.run_gate()
        self.assertEqual(code, 1, out)
        self.assertIn("[content-path]", out)


if __name__ == "__main__":
    unittest.main()
