# shared — the host-neutral .NET code

The .NET code no host API touches, which the Revit and AutoCAD plugins both build from. Nothing
here is a plugin: each host's shell references these projects and ships its own copy of their
assemblies beside its own (`HPS-43`).

```
src/MantlePlace.Core/          PURE. Protocol behaviour: parsing, validity decisions, plans, state. No host API, no I/O, no NuGet. net8.0.
src/MantlePlace.Client/        IMPURE, but not a host. HTTP, the loopback listener, the bundle cache, secrets, zip. net8.0.
src/MantlePlace.PlatformApi/   The generated Platform API client and its transport. net8.0.
src/MantlePlace.Wpf/           IMPURE (WPF), but not a host. The windows every host opens: sign-in, the vault
                               browser, About, the notices, their brand chrome and window styles. net8.0-windows.
tests/MantlePlace.Core.Tests/  The headless suite over Core AND Client. net8.0 + net10.0.
tools/platform-api/            The script that re-vendors the Platform API spec and client.
```

- **No host in a name.** No project, namespace or folder here names a host. Code that needs a
  host's assemblies, or that only one host ever runs, belongs in that host's tree.
- **The host names itself at load.** The platform tells the plugins apart by `HostIdentity.Id`,
  which each shell sets from a module initializer (`HostRegistration.cs` in
  [`revit/`](../revit/src/MantlePlace.Revit.Addin/HostRegistration.cs) and
  [`autocad/`](../autocad/src/MantlePlace.AutoCad.Addin/HostRegistration.cs)) with its own version.
  Nothing here is compiled differently for one host.
- **One set of windows.** A host opens the windows in `MantlePlace.Wpf` and supplies only what is its
  own: the owner window, its import from the vault where it has one, its feedback launcher and its
  About sentences and commands. Its name in a label comes from `HostIdentity`, and the labels and
  their order are decided in `MantlePlace.Core` (`WindowLabels`, `AboutPage`), where
  `SharedWindowsTests` holds them for both hosts and checks neither host tree holds a copy. No window
  shows a host's logo: each sets the Mantle Place mark in its title bar.
- **No version of its own.** A plugin's version has one home per host; what the platform is told is
  the version the host hands `HostIdentity`.

## Build and test

No host install is needed for the suite; `MantlePlace.Wpf` builds with each host's shell, on
Windows:

```bash
dotnet run --project shared/tests/MantlePlace.Core.Tests/MantlePlace.Core.Tests.csproj -f net8.0
dotnet run --project shared/tests/MantlePlace.Core.Tests/MantlePlace.Core.Tests.csproj -f net10.0
```

The suite runs as the Revit plugin, the host its cases were written against, and `ci-revit-tests`
runs it. The AutoCAD suite compiles in its harness and conformance runners and drives them as
AutoCAD (`ci-autocad-tests`). The Platform API client is re-vendored with
`python shared/tools/platform-api/sync_platform_api.py` ([`docs/agents/ci.md`](../docs/agents/ci.md)).
