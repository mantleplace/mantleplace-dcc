// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceEditorSettings.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceEditorSettingsTest,
    "MantlePlace.Import.EditorSettings",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceEditorSettingsTest::RunTest(const FString& Parameters)
{
	using FSettings = UMantlePlaceEditorSettings;

	{
		TestEqual(TEXT("the first stream port is still 8088"),
			FSettings::DefaultLocalTileServerFirstPort, 8088);
		TestEqual(TEXT("the scan still covers eight ports, i.e. 8088-8095"),
			FSettings::DefaultLocalTileServerPortScanCount, 8);
		TestEqual(TEXT("the poll interval is still three seconds"),
			FSettings::DefaultMaterializePollIntervalSeconds, 3.0f);
		TestEqual(TEXT("the poll budget is still two hundred"),
			FSettings::DefaultMaterializeMaxPolls, 200);
		TestEqual(TEXT("the failure tolerance is still five"),
			FSettings::DefaultMaxConsecutivePollFailures, 5);
		TestEqual(TEXT("the World Partition grid size is the engine's default of two"),
			FSettings::DefaultWorldPartitionGridSize, 2);
	}

	{
		TestEqual(TEXT("a usable grid size is honoured"), FSettings::UsableWorldPartitionGridSize(4), 4);
		TestEqual(TEXT("one component per proxy is usable"), FSettings::UsableWorldPartitionGridSize(1), 1);
		TestEqual(TEXT("sixteen is the engine's ceiling and usable"), FSettings::UsableWorldPartitionGridSize(16), 16);
		TestEqual(TEXT("zero falls back"), FSettings::UsableWorldPartitionGridSize(0), 2);
		TestEqual(TEXT("past the engine's ceiling falls back"), FSettings::UsableWorldPartitionGridSize(17), 2);
		TestEqual(TEXT("a negative grid size falls back"), FSettings::UsableWorldPartitionGridSize(-3), 2);
		const int32 Resolved = FSettings::ResolveWorldPartitionGridSize();
		TestTrue(TEXT("the resolved grid size is in the engine's range"), Resolved >= 1 && Resolved <= 16);
	}

	{
		TestEqual(TEXT("a usable port is honoured"),
			FSettings::UsableLocalTileServerFirstPort(9000), 9000);
		TestEqual(TEXT("the bottom of the unprivileged range is usable"),
			FSettings::UsableLocalTileServerFirstPort(1024), 1024);
		TestEqual(TEXT("the top of the port space is usable"),
			FSettings::UsableLocalTileServerFirstPort(65535), 65535);

		TestEqual(TEXT("a privileged port falls back"),
			FSettings::UsableLocalTileServerFirstPort(80),
			FSettings::DefaultLocalTileServerFirstPort);
		TestEqual(TEXT("zero falls back"),
			FSettings::UsableLocalTileServerFirstPort(0),
			FSettings::DefaultLocalTileServerFirstPort);
		TestEqual(TEXT("a negative port falls back"),
			FSettings::UsableLocalTileServerFirstPort(-1),
			FSettings::DefaultLocalTileServerFirstPort);
		TestEqual(TEXT("a port past the end of the space falls back"),
			FSettings::UsableLocalTileServerFirstPort(70000),
			FSettings::DefaultLocalTileServerFirstPort);
	}

	{
		TestEqual(TEXT("a usable count is honoured"),
			FSettings::UsableLocalTileServerPortScanCount(8088, 3), 3);
		TestEqual(TEXT("one port is a usable scan"),
			FSettings::UsableLocalTileServerPortScanCount(8088, 1), 1);
		TestEqual(TEXT("zero falls back to the default count"),
			FSettings::UsableLocalTileServerPortScanCount(8088, 0),
			FSettings::DefaultLocalTileServerPortScanCount);
		TestEqual(TEXT("a negative count falls back"),
			FSettings::UsableLocalTileServerPortScanCount(8088, -5),
			FSettings::DefaultLocalTileServerPortScanCount);

		TestEqual(TEXT("a scan from the last port covers exactly that port"),
			FSettings::UsableLocalTileServerPortScanCount(65535, 8), 1);
		TestEqual(TEXT("a scan near the top is clamped to what is left"),
			FSettings::UsableLocalTileServerPortScanCount(65530, 100), 6);
		TestEqual(TEXT("an unusable first port does not shrink the scan"),
			FSettings::UsableLocalTileServerPortScanCount(-1, 8), 8);
	}

	{
		TestEqual(TEXT("a usable interval is honoured"),
			FSettings::UsableMaterializePollIntervalSeconds(10.0f), 10.0f);
		TestEqual(TEXT("the half-second floor is usable"),
			FSettings::UsableMaterializePollIntervalSeconds(0.5f), 0.5f);

		TestEqual(TEXT("below the floor falls back"),
			FSettings::UsableMaterializePollIntervalSeconds(0.1f),
			FSettings::DefaultMaterializePollIntervalSeconds);
		TestEqual(TEXT("zero falls back"),
			FSettings::UsableMaterializePollIntervalSeconds(0.0f),
			FSettings::DefaultMaterializePollIntervalSeconds);
		TestEqual(TEXT("negative falls back"),
			FSettings::UsableMaterializePollIntervalSeconds(-3.0f),
			FSettings::DefaultMaterializePollIntervalSeconds);
		TestEqual(TEXT("an absurdly long interval falls back"),
			FSettings::UsableMaterializePollIntervalSeconds(100000.0f),
			FSettings::DefaultMaterializePollIntervalSeconds);
	}

	{
		TestEqual(TEXT("a usable budget is honoured"), FSettings::UsableMaterializeMaxPolls(50), 50);
		TestEqual(TEXT("one poll is a usable budget"), FSettings::UsableMaterializeMaxPolls(1), 1);

		TestEqual(TEXT("zero polls falls back"),
			FSettings::UsableMaterializeMaxPolls(0), FSettings::DefaultMaterializeMaxPolls);
		TestEqual(TEXT("a negative budget falls back"),
			FSettings::UsableMaterializeMaxPolls(-1), FSettings::DefaultMaterializeMaxPolls);

		TestEqual(TEXT("a usable tolerance is honoured"),
			FSettings::UsableMaxConsecutivePollFailures(2), 2);
		TestEqual(TEXT("zero tolerance means zero, not the default"),
			FSettings::UsableMaxConsecutivePollFailures(0), 0);
		TestEqual(TEXT("a negative tolerance falls back"),
			FSettings::UsableMaxConsecutivePollFailures(-1),
			FSettings::DefaultMaxConsecutivePollFailures);
	}

	{
		int32 FirstPort = 0;
		int32 PortCount = 0;
		FSettings::ResolveLocalTileServerPortScan(FirstPort, PortCount);
		TestTrue(TEXT("the resolved port scan is a usable range"),
			FirstPort >= 1024 && FirstPort <= 65535 && PortCount >= 1);
		TestTrue(TEXT("and does not run off the end of the port space"),
			FirstPort + PortCount - 1 <= 65535);

		TestTrue(TEXT("the resolved poll interval is at least the floor"),
			FSettings::ResolveMaterializePollIntervalSeconds() >= 0.5f);
		TestTrue(TEXT("the resolved poll budget asks at least once"),
			FSettings::ResolveMaterializeMaxPolls() >= 1);
		TestTrue(TEXT("the resolved failure tolerance is not negative"),
			FSettings::ResolveMaxConsecutivePollFailures() >= 0);
	}

	return true;
}

#endif
