# Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.
"""Every Platform API request the plugin's auth and vault clients make carries the client header.

`Mantle-Place-Client: unreal/<plugin version>` goes on every call because the clients create their
requests through `FMantlePlaceClientHttp::CreateRequest`, which sets it. CI never compiles this
plugin, so nothing but this text check stops a later edit from calling `FHttpModule::CreateRequest`
directly in one of the two clients and quietly sending a call that names no client. The factory's
own behaviour is asserted by the `MantlePlace.Client.Header` automation test, where an engine exists.

A request that is not a Platform API call (the ranged probe of a presigned storage URL) marks its
line `// not a Platform API call`; nothing else may bypass the factory.
"""

import re
import unittest
from pathlib import Path

PRIVATE = (
    Path(__file__).resolve().parent.parent
    / "MantlePlace" / "Source" / "MantlePlaceRuntime" / "Private"
)

# The two clients that talk to the Platform API. The bundle cache downloads from a presigned URL.
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
        # list, mint (download), materialize start, materialize status: four Platform API calls.
        uses = [line for line in lines_of("MantlePlaceVaultClient.cpp") if FACTORY.search(line)]
        self.assertEqual(len(uses), 4)

    def test_the_gate_sees_a_bypass(self):
        # The check itself: an unmarked direct call is found, a marked one is not.
        self.assertEqual(
            bypasses(["ok();", "A = FHttpModule::Get().CreateRequest();"]), [2])
        self.assertEqual(
            bypasses(["A = FHttpModule::Get().CreateRequest(); // not a Platform API call"]), [])
        self.assertEqual(
            bypasses(["A = FMantlePlaceClientHttp::CreateRequest(TEXT(\"GET\"), Url);"]), [])


if __name__ == "__main__":
    unittest.main()
