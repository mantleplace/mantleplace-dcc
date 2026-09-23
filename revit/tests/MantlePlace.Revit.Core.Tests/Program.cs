using MantlePlace.Revit.Core.Tests;

// Headless entry point: no Revit, no test framework, no network (HPS-42). Non-zero exit on any
// failing assertion, so CI needs nothing but `dotnet run`.
int exitCode = 0;
exitCode |= ManifestConformanceTests.Run();
exitCode |= ConformanceCorpusSelfTests.Run();
exitCode |= ManifestReaderTests.Run();
exitCode |= ImportPlannerTests.Run();
exitCode |= ImportLayerTests.Run();
exitCode |= SurfacePointsTests.Run();
exitCode |= SurfaceTinTests.Run();
exitCode |= PublishedContourTests.Run();
exitCode |= SiteVectorTests.Run();
exitCode |= HostFramePlacementTests.Run();
exitCode |= ProjectionConformanceTests.Run();
exitCode |= CacheKeySanitiserTests.Run();
exitCode |= ImportStepLifetimeTests.Run();
exitCode |= SiteCompanionPathTests.Run();
exitCode |= PngHeaderTests.Run();
exitCode |= SiteBoundaryIdentityTests.Run();
exitCode |= SiteBoundaryCoextensionTests.Run();
exitCode |= TerrainIdentityTests.Run();
exitCode |= TreeIdentityTests.Run();
exitCode |= TreeFamilyTests.Run();
exitCode |= ShrubFamilyTests.Run();
exitCode |= PlantingSummaryTests.Run();
exitCode |= FamilyFileStoreTests.Run();
exitCode |= AttributionTests.Run();
exitCode |= BuildingIdentityTests.Run();
exitCode |= SiteModelReaderTests.Run();
exitCode |= SiteContextTests.Run();
exitCode |= DrapeLayeringTests.Run();
exitCode |= SubDivisionDrapeTests.Run();
exitCode |= SubDivisionMaterialTests.Run();
exitCode |= RendererKeywordsTests.Run();
exitCode |= GroundCutsTests.Run();
exitCode |= RoadIdentityTests.Run();
exitCode |= TerrainSmoothingTests.Run();
exitCode |= DrapeAnchorTests.Run();
exitCode |= SurfaceAgreementTests.Run();
exitCode |= TerrainBaseTests.Run();
exitCode |= SurfaceSanitiserTests.Run();
exitCode |= ImportFailurePolicyTests.Run();
exitCode |= SlowStepNoticeTests.Run();
exitCode |= StagedImportTests.Run();
exitCode |= ImportChunkingTests.Run();
exitCode |= ReadinessReasonTests.Run();
exitCode |= LocalBundleArchiveTests.Run();
exitCode |= OpenLogsTests.Run();
exitCode |= VectorDocumentSelfTests.Run();
exitCode |= AddinFaultTests.Run();
exitCode |= InstalledBuildTests.Run();
exitCode |= AuthConformanceTests.Run();
exitCode |= AuthClientTests.Run();
exitCode |= AccountRibbonTests.Run();
exitCode |= RibbonImageryTests.Run();
exitCode |= WindowLabelsTests.Run();
exitCode |= VaultRowsTests.Run();
exitCode |= DeliveryHeaderTests.Run();
exitCode |= VaultConformanceTests.Run();
exitCode |= MaterializeJobTests.Run();
exitCode |= PrepareNoticeTests.Run();
exitCode |= PrepareWatcherTests.Run();
exitCode |= VaultNewsTests.Run();
exitCode |= AnnouncedOrderStoreTests.Run();
exitCode |= VaultNewsCheckerTests.Run();
exitCode |= CacheConformanceTests.Run();
exitCode |= BundleCacheTests.Run();

if (exitCode == 0)
{
    Console.WriteLine("OK: Revit pure cores conform to the shared corpus and the import policy holds.");
}

return exitCode;
