# Security policy

## Reporting a vulnerability

**Report privately, through GitHub's private vulnerability reporting.** Open the
[Security tab](../../security/advisories/new) of this repository and file a draft advisory. That
channel is private between you and the maintainers until an advisory is published.

If GitHub is unavailable to you, email **support@mantle.place** with `SECURITY` in the subject.

**Please do not open a public issue for a vulnerability**, and please do not open a pull request that
fixes one — a fix in a public PR is a disclosure, and it lands before anyone can ship the mitigation.
See "report, don't patch" below.

Useful in a report: what an attacker can do, the affected host and version, and the smallest thing
that reproduces it. A proof of concept helps and is never required.

## What we will do

This project's maintenance posture is **best-effort with no SLA** — the same posture stated in
[CONTRIBUTING.md](CONTRIBUTING.md), applied honestly to security. What that means concretely:

- we aim to acknowledge a report within **one week**;
- we will tell you what we assess the severity to be, and we will tell you if we disagree with yours;
- when a fix ships we publish an advisory and credit you, unless you ask us not to;
- if we cannot turn a report around in reasonable time, we will say so rather than let it go quiet.

We do not run a bug bounty.

## Report, don't patch: auth and the secret store

Two areas are **closed to outside patches**:

- everything under the authentication flow — PKCE, the loopback redirect listener, the token grant
  and the auth state machine;
- the secret stores that hold the refresh token (`SecretStore.cs`, `MantlePlaceSecretStore.cpp`).

A defect there is a security report, not a pull request. This is not distrust: it is that a subtle
change in these files is very hard to review as *safe* from the outside, and a mistake costs a user
their session rather than their build. Report it and we will write the fix.

Everything else in this repository takes patches like any other code.

## Scope

**In scope:** the plugin code in this repository — the Unreal plugin under `unreal/`, the Revit
plugin under `revit/`, and the conformance tooling under `tools/`.

**Out of scope here:** the Mantle Place platform and web application at `mantle.place`. Those are
separate systems; report issues in them to **support@mantle.place** rather than through this
repository's advisory channel, so they reach the right people.

## What this software does with your credentials

Stated plainly, so a report can be measured against intent:

- sign-in is **OAuth 2.0 Authorization Code + PKCE (S256) through the system browser**. The plugin
  never sees a password and never embeds a web view for login.
- the redirect is captured on a `127.0.0.1` loopback listener that is bound **before** the browser
  opens.
- the **access token is memory-only** and is never written to disk.
- the **refresh token** is stored per-OS-user through the platform's own secret store (DPAPI on
  Windows). A machine with no secure store degrades to memory-only auth and says so, rather than
  writing the token somewhere less safe.
- importing a local bundle requires **no sign-in, no licence check and no server call**.

If you find any of those five statements to be false, that is a vulnerability report and we want it.
