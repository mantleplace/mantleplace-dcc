// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceEditorSettings.h"

// The numbers the plugin used to hardcode are project settings now, and a setting arrives from a
// checked-in ini file that nothing validates on the way in. The ClampMin/ClampMax meta specifiers
// constrain the Project Settings UI only — a value hand-edited into DefaultGame.ini, or carried over
// from an older plugin version, reaches the code unchecked.
//
// So each one has a usability rule, and each rule is asserted here rather than being a clamp inline
// at a call site where nobody can see it. The shape is the same as the content root's: an unusable
// value falls back to the shipped default rather than failing the operation, because a stream that
// cannot start and an import that times out before asking once are both worse than a number the
// user did not choose.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceEditorSettingsTest,
    "MantlePlace.Import.EditorSettings",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceEditorSettingsTest::RunTest(const FString& Parameters)
{
	using FSettings = UMantlePlaceEditorSettings;

	// --- The defaults are the literals the code used to hardcode --------------------------------
	// Asserted literally, like the naming test asserts the strings the importer writes: an edit to
	// one of these changes what an unconfigured project does, and that edit IS the decision.
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
	}

	// --- Ports ------------------------------------------------------------------------------------
	{
		TestEqual(TEXT("a usable port is honoured"),
			FSettings::UsableLocalTileServerFirstPort(9000), 9000);
		TestEqual(TEXT("the bottom of the unprivileged range is usable"),
			FSettings::UsableLocalTileServerFirstPort(1024), 1024);
		TestEqual(TEXT("the top of the port space is usable"),
			FSettings::UsableLocalTileServerFirstPort(65535), 65535);

		// A privileged port is not a preference to honour: the editor does not run as root on any
		// platform this plugin supports, so binding it fails and the stream never starts.
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

	// --- The scan count ---------------------------------------------------------------------------
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

		// The scan must not run off the end of the port space. Clamped rather than refused: a scan
		// starting near the top still has ports worth trying.
		TestEqual(TEXT("a scan from the last port covers exactly that port"),
			FSettings::UsableLocalTileServerPortScanCount(65535, 8), 1);
		TestEqual(TEXT("a scan near the top is clamped to what is left"),
			FSettings::UsableLocalTileServerPortScanCount(65530, 100), 6);
		// An unusable first port is resolved before the room is measured, so the count is measured
		// against the port that will actually be scanned.
		TestEqual(TEXT("an unusable first port does not shrink the scan"),
			FSettings::UsableLocalTileServerPortScanCount(-1, 8), 8);
	}

	// --- The poll interval --------------------------------------------------------------------------
	{
		TestEqual(TEXT("a usable interval is honoured"),
			FSettings::UsableMaterializePollIntervalSeconds(10.0f), 10.0f);
		TestEqual(TEXT("the half-second floor is usable"),
			FSettings::UsableMaterializePollIntervalSeconds(0.5f), 0.5f);

		// Below the floor the polls cost more than the wait they measure; zero or negative is a busy
		// loop against the platform. This floor used to be a FMath::Max inline at the scheduler.
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

	// --- The poll budget and the failure tolerance ----------------------------------------------------
	{
		TestEqual(TEXT("a usable budget is honoured"), FSettings::UsableMaterializeMaxPolls(50), 50);
		TestEqual(TEXT("one poll is a usable budget"), FSettings::UsableMaterializeMaxPolls(1), 1);

		// Zero polls is not "wait forever"; it times out before asking once, which reads to a user as
		// an import that failed instantly for no reason.
		TestEqual(TEXT("zero polls falls back"),
			FSettings::UsableMaterializeMaxPolls(0), FSettings::DefaultMaterializeMaxPolls);
		TestEqual(TEXT("a negative budget falls back"),
			FSettings::UsableMaterializeMaxPolls(-1), FSettings::DefaultMaterializeMaxPolls);

		TestEqual(TEXT("a usable tolerance is honoured"),
			FSettings::UsableMaxConsecutivePollFailures(2), 2);
		// Zero IS meaningful here, unlike the budget: fail on the first failed status poll.
		TestEqual(TEXT("zero tolerance means zero, not the default"),
			FSettings::UsableMaxConsecutivePollFailures(0), 0);
		TestEqual(TEXT("a negative tolerance falls back"),
			FSettings::UsableMaxConsecutivePollFailures(-1),
			FSettings::DefaultMaxConsecutivePollFailures);
	}

	// --- An unconfigured project behaves exactly as the hardcoded plugin did ------------------------
	// The whole point of the exercise: configurability that changes nothing until someone configures.
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

#endif // WITH_DEV_AUTOMATION_TESTS
