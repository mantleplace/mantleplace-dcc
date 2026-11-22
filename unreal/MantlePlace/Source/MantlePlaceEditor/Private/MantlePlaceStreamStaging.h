// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

namespace MantlePlaceStreamStaging
{
	constexpr int32 CurrentSchemeVersion = 2;

	struct FRecord
	{
		FString Identity;

		FString ManifestSha256;

		FString TerrainPrefix;

		FString CesiumTerrainPath;

		FString DrapePath;

		int32 EntryCount = 0;

		int32 SchemeVersion = 0;
	};

	enum class EVerdict : uint8
	{
		Stage,

		Reuse,
	};

	EVerdict Classify(
		bool bRecordFound,
		const FRecord& Found,
		const FRecord& Incoming,
		bool bTerrainRootPresent);

	bool SelectsEntry(const FRecord& Record, const FString& EntryName);

	FString ToJson(const FRecord& Record);
	bool FromJson(const FString& Json, FRecord& OutRecord);

	FString StagingDir(const FString& Identity);

	FString RecordPath(const FString& Identity);

	bool Read(const FString& Identity, FRecord& OutRecord);
	bool Write(const FRecord& Record);
}
