// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceEditorSettings.h"

#include "MantlePlaceImportNaming.h"

#include "Math/UnrealMathUtility.h"  // FMath::Clamp
#include "UObject/UObjectGlobals.h"  // GetDefault

UMantlePlaceEditorSettings::UMantlePlaceEditorSettings()
	: GeneratedContentRoot(MantlePlaceImportNaming::DefaultContentRoot())
	, LocalTileServerFirstPort(DefaultLocalTileServerFirstPort)
	, LocalTileServerPortScanCount(DefaultLocalTileServerPortScanCount)
	, MaterializePollIntervalSeconds(DefaultMaterializePollIntervalSeconds)
	, MaterializeMaxPolls(DefaultMaterializeMaxPolls)
	, MaxConsecutivePollFailures(DefaultMaxConsecutivePollFailures)
{
}

int32 UMantlePlaceEditorSettings::UsableLocalTileServerFirstPort(int32 Configured)
{
	// Unprivileged ports only. The editor does not run as root on any platform this plugin
	// supports, so a configured 80 is not a preference to honour — it is a bind that fails, and
	// failing back to a port that works beats a stream that cannot start.
	return (Configured >= 1024 && Configured <= 65535) ? Configured : DefaultLocalTileServerFirstPort;
}

int32 UMantlePlaceEditorSettings::UsableLocalTileServerPortScanCount(int32 FirstPort, int32 Configured)
{
	if (Configured < 1)
	{
		Configured = DefaultLocalTileServerPortScanCount;
	}
	// Never scan past the end of the port space: FirstPort is already clamped to <= 65535, so the
	// remaining room is what bounds the count. Clamped rather than refused — a scan that would run
	// off the end still has ports worth trying.
	const int32 Room = 65535 - UsableLocalTileServerFirstPort(FirstPort) + 1;
	return FMath::Clamp(Configured, 1, Room);
}

float UMantlePlaceEditorSettings::UsableMaterializePollIntervalSeconds(float Configured)
{
	// The half-second floor is the one the scheduler already applied inline. It is here now so the
	// reason is stated once: below it the polls cost more than the wait they are measuring, and a
	// zero or negative interval is a busy loop against the platform.
	return (Configured >= 0.5f && Configured <= 600.0f)
		? Configured
		: DefaultMaterializePollIntervalSeconds;
}

int32 UMantlePlaceEditorSettings::UsableMaterializeMaxPolls(int32 Configured)
{
	// Zero or fewer polls is not "wait forever", it is "time out before asking once", which reads to
	// a user as an import that failed instantly for no reason.
	return Configured >= 1 ? Configured : DefaultMaterializeMaxPolls;
}

int32 UMantlePlaceEditorSettings::UsableMaxConsecutivePollFailures(int32 Configured)
{
	// Zero is meaningful here, unlike above: it means fail on the first failed status poll.
	return Configured >= 0 ? Configured : DefaultMaxConsecutivePollFailures;
}

void UMantlePlaceEditorSettings::ResolveLocalTileServerPortScan(int32& OutFirstPort, int32& OutPortCount)
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	const int32 ConfiguredFirst =
		Settings != nullptr ? Settings->LocalTileServerFirstPort : DefaultLocalTileServerFirstPort;
	const int32 ConfiguredCount = Settings != nullptr
		? Settings->LocalTileServerPortScanCount
		: DefaultLocalTileServerPortScanCount;

	OutFirstPort = UsableLocalTileServerFirstPort(ConfiguredFirst);
	OutPortCount = UsableLocalTileServerPortScanCount(ConfiguredFirst, ConfiguredCount);
}

float UMantlePlaceEditorSettings::ResolveMaterializePollIntervalSeconds()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return UsableMaterializePollIntervalSeconds(Settings != nullptr
		? Settings->MaterializePollIntervalSeconds
		: DefaultMaterializePollIntervalSeconds);
}

int32 UMantlePlaceEditorSettings::ResolveMaterializeMaxPolls()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return UsableMaterializeMaxPolls(
		Settings != nullptr ? Settings->MaterializeMaxPolls : DefaultMaterializeMaxPolls);
}

int32 UMantlePlaceEditorSettings::ResolveMaxConsecutivePollFailures()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return UsableMaxConsecutivePollFailures(
		Settings != nullptr ? Settings->MaxConsecutivePollFailures : DefaultMaxConsecutivePollFailures);
}

FString UMantlePlaceEditorSettings::ResolveGeneratedContentRoot()
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	// GetDefault on a UDeveloperSettings does not return null in a running editor, but the importer
	// is also driven from automation and from Blueprint, so the fallback is stated rather than
	// assumed. Validation lives in the naming module, where it is asserted headlessly.
	return MantlePlaceImportNaming::ResolveContentRoot(
		Settings != nullptr ? Settings->GeneratedContentRoot : FString());
}

FName UMantlePlaceEditorSettings::GetCategoryName() const
{
	// Under Plugins, not Project: this configures a plugin, and a reader looking for it will look
	// where the other plugins are.
	return TEXT("Plugins");
}
