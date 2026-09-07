---
name: adr-0005-release-installs-are-copy-first
description: The Revit release tells a curator to copy the files first and offers the installer script second, because execution policy and Mark-of-the-Web block a downloaded script on the machines this ships into — even though the maintainer deploy script argues the reverse. Read before editing the packaging instructions, the installer, or the deploy script's guidance.
status: accepted
---

# The Revit release installs by copy first, script second

`revit/packaging/README.txt` leads with "copy the files into this folder" and offers `Install.cmd`
only underneath it. This directly contradicts `revit/tools/Deploy-MantlePlaceRevit.ps1`, whose own
documentation argues that hand-copying is how a machine ends up running a plugin months older than
the source tree — so it is written down here, because the obvious reconciliation is the wrong one.

Both are right about different people. **The staleness hazard is a maintainer's, not a curator's.**
A maintainer copies from a moving `bin/` many times a week, and a stale copy produces a bug that
reproduces against code already containing its fix, with a file timestamp as the only tell. A
curator installs a fixed version once: install `0.1.0` and it stays `0.1.0`, because there is no
source tree for it to drift from. The failure the script prevents does not exist on the release path.

Meanwhile a `.ps1` in a downloaded zip is not something a curator can reliably run. The default
execution policy on Windows client is `Restricted`, so double-clicking a script opens Notepad; and
Windows stamps Mark-of-the-Web onto files extracted from a download, so even a machine relaxed to
`RemoteSigned` blocks it until somebody knows to right-click and Unblock. `Install.cmd` clears both
with `-ExecutionPolicy Bypass`, but it cannot clear a **Group Policy** execution policy, which
overrides the command line — and that is set by exactly the AEC IT departments this plugin ships
into. A copy into `%APPDATA%` needs no policy, no admin, no UAC prompt and no .NET install, and
therefore works on every machine including the ones we will never see.

## Consequences

- The script is not removed and does not fork. `Deploy-MantlePlaceRevit.ps1` gained
  `-PayloadDirectory` and ships verbatim in the zip, so the file a curator runs is the file
  maintainers run daily. That is the only test coverage it can have: CI cannot execute it, because
  CI cannot build this add-in at all.
- `Install.cmd` must `pause`. Launched from Explorer it gets its own console window, and without the
  pause that window closes on the last line — the curator sees neither the success nor the reason.
- When scripts are blocked, the installer says so and points at the manual steps rather than
  suggesting a workaround. Talking someone through relaxing their execution policy is worse advice
  than "copy these files", and it is advice their IT department has already refused.
