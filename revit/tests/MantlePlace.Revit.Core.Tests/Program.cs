using MantlePlace.Revit.Core.Tests;

// Headless entry point: no Revit, no test framework, no network (HPS-42). Non-zero exit on any
// failing assertion, so CI needs nothing but `dotnet run`.
int exitCode = 0;
exitCode |= ManifestConformanceTests.Run();
exitCode |= ConformanceCorpusSelfTests.Run();
exitCode |= ManifestReaderTests.Run();
exitCode |= ImportPlannerTests.Run();
exitCode |= SurfacePointsTests.Run();
exitCode |= SiteVectorTests.Run();
exitCode |= ProjectionConformanceTests.Run();
exitCode |= CacheKeySanitiserTests.Run();
exitCode |= ImportStepLifetimeTests.Run();
exitCode |= PngHeaderTests.Run();
exitCode |= SiteBoundaryIdentityTests.Run();
exitCode |= DrapeLayeringTests.Run();
exitCode |= ReadinessReasonTests.Run();
exitCode |= LocalBundleArchiveTests.Run();
exitCode |= VectorDocumentSelfTests.Run();
exitCode |= AuthConformanceTests.Run();
exitCode |= AuthClientTests.Run();
exitCode |= VaultConformanceTests.Run();
exitCode |= CacheConformanceTests.Run();
exitCode |= BundleCacheTests.Run();

if (exitCode == 0)
{
    Console.WriteLine("OK: Revit pure cores conform to the shared corpus and the import policy holds.");
}

return exitCode;
