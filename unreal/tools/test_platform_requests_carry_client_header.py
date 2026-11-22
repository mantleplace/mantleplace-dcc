# Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

import re
import unittest
from pathlib import Path

PRIVATE = (
    Path(__file__).resolve().parent.parent
    / "MantlePlace" / "Source" / "MantlePlaceRuntime" / "Private"
)

CLIENTS = ("MantlePlaceAuthSystemBase.cpp", "MantlePlaceVaultClient.cpp")

DIRECT = re.compile(r"FHttpModule::Get\(\)\s*\.\s*CreateRequest\s*\(")
FACTORY = re.compile(r"FMantlePlaceClientHttp::CreateRequest\s*\(")
MARK = "not a Platform API call"


def lines_of(name):
    return (PRIVATE / name).read_text(encoding="utf-8").splitlines()


def bypasses(text_lines):
    return [
        number
        for number, line in enumerate(text_lines, start=1)
        if DIRECT.search(line) and MARK not in line
    ]


class ClientHeaderIsOnEveryRequest(unittest.TestCase):
    def test_clients_create_requests_through_the_factory(self):
        for name in CLIENTS:
            with self.subTest(client=name):
                self.assertEqual(
                    bypasses(lines_of(name)), [],
                    f"{name} creates a request without the client header; use "
                    "FMantlePlaceClientHttp::CreateRequest, or mark a non-Platform call "
                    f"'// {MARK}'",
                )

    def test_every_client_uses_the_factory_at_least_once(self):
        for name in CLIENTS:
            with self.subTest(client=name):
                uses = [line for line in lines_of(name) if FACTORY.search(line)]
                self.assertTrue(uses, f"{name} no longer goes through the client factory")

    def test_the_vault_client_sends_one_per_platform_call(self):
        uses = [line for line in lines_of("MantlePlaceVaultClient.cpp") if FACTORY.search(line)]
        self.assertEqual(len(uses), 4)

    def test_the_gate_sees_a_bypass(self):
        self.assertEqual(
            bypasses(["ok();", "A = FHttpModule::Get().CreateRequest();"]), [2])
        self.assertEqual(
            bypasses(["A = FHttpModule::Get().CreateRequest(); // not a Platform API call"]), [])
        self.assertEqual(
            bypasses(["A = FMantlePlaceClientHttp::CreateRequest(TEXT(\"GET\"), Url);"]), [])


if __name__ == "__main__":
    unittest.main()
