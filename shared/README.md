# shared — the host-neutral .NET code

The .NET code no host API touches, which the Revit and AutoCAD plugins both build from. Nothing
here is a plugin: each host's shell references these projects and ships its own copy of their
assemblies beside its own (`HPS-43`).

```
src/MantlePlace.Core/          PURE. Protocol behaviour: parsing, validity decisions, plans, state. No host API, no I/O, no NuGet. net8.0.
src/MantlePlace.Client/        IMPURE, but not a host. HTTP, the loopback listener, the bundle cache, secrets, zip. net8.0.
src/MantlePlace.PlatformApi/   The generated Platform API client and its transport. net8.0.
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
- **No version of its own.** A plugin's version has one home per host; what the platform is told is
  the version the host hands `HostIdentity`.

## Build and test

No host install is needed:

```bash
dotnet run --project shared/tests/MantlePlace.Core.Tests/MantlePlace.Core.Tests.csproj -f net8.0
dotnet run --project shared/tests/MantlePlace.Core.Tests/MantlePlace.Core.Tests.csproj -f net10.0
```

The suite runs as the Revit plugin, the host its cases were written against, and `ci-revit-tests`
runs it. The AutoCAD suite compiles in its harness and conformance runners and drives them as
AutoCAD (`ci-autocad-tests`). The Platform API client is re-vendored with
`python shared/tools/platform-api/sync_platform_api.py` ([`docs/agents/ci.md`](../docs/agents/ci.md)).
