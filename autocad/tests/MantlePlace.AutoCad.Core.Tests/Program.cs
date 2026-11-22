using System.Reflection;
using MantlePlace.AutoCad;
using MantlePlace.AutoCad.Tests;
using MantlePlace.Core;
using MantlePlace.Core.Tests;

HostIdentity.Set(
    AutoCadBlockReader.HostKey,
    typeof(AutoCadBlockReader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    ManifestVersions.AutoCadNewestImportedMajor);

int exitCode = 0;
exitCode |= ConformanceCorpusSelfTests.Run();
exitCode |= VaultConformanceTests.Run();
exitCode |= AuthConformanceTests.Run();
exitCode |= CacheConformanceTests.Run();
exitCode |= ProjectionConformanceTests.Run();
exitCode |= PlatformConformanceTests.Run();
exitCode |= ReaderConformanceTests.Run();
exitCode |= AutoCadManifestConformanceTests.Run();
exitCode |= AutoCadImportPlanTests.Run();
exitCode |= AutoCadThreePlanTests.Run();
exitCode |= AutoCadImportRunTests.Run();
exitCode |= DownloadLevelTests.Run();
exitCode |= AutoCadRecordSetTests.Run();
exitCode |= LandXmlSurfaceTests.Run();
exitCode |= AutoCadBundleManifestTests.Run();
exitCode |= PresenceTests.Run();
exitCode |= HostIdentityTests.Run();
exitCode |= PlugInsPanelTests.Run();
exitCode |= ExportReceiptTests.Run();
exitCode |= LocalBundleImportTests.Run();
exitCode |= AboutPageTests.Run();
exitCode |= HostReleaseYearTests.Run();
exitCode |= ShimWindowTests.Run("autocad/src/MantlePlace.AutoCad.Addin", "shim windows", declaresWindows: false);
exitCode |= SharedWindowsTests.Run();

if (exitCode == 0)
{
    Console.WriteLine("OK: the AutoCAD plugin's shared sources conform to the corpus and its own decisions hold.");
}

return exitCode;
