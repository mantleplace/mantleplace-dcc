using MantlePlace.AutoCad.Tests;
using MantlePlace.Revit.Core.Tests;

// Headless entry point: no AutoCAD, no Civil 3D, no test framework, no network (HPS-42). Non-zero
// exit on any failing assertion, so CI needs nothing but `dotnet run`.
//
// The conformance runners are the Revit suite's, compiled in (the csproj says why). This host claims
// every corpus group except `manifest`: its `hosts.autocad` block has no reader yet, so the four
// cases scoped to `autocad` are not asserted here and the registration does not claim them.
int exitCode = 0;
exitCode |= ConformanceCorpusSelfTests.Run();
exitCode |= VaultConformanceTests.Run();
exitCode |= AuthConformanceTests.Run();
exitCode |= CacheConformanceTests.Run();
exitCode |= ProjectionConformanceTests.Run();
exitCode |= PlatformConformanceTests.Run();
exitCode |= AutoCadBundleManifestTests.Run();
exitCode |= PresenceTests.Run();
exitCode |= HostIdentityTests.Run();
exitCode |= PlugInsPanelTests.Run();
exitCode |= ExportReceiptTests.Run();
exitCode |= AboutPageTests.Run();

if (exitCode == 0)
{
    Console.WriteLine("OK: the AutoCAD plugin's shared sources conform to the corpus and its own decisions hold.");
}

return exitCode;
