// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

#include "MantlePlaceCuratorExtentLogic.h"

namespace MantlePlaceImportProvenance
{
	constexpr int32 CurrentSchemeVersion = 1;

	struct FRecord
	{
		FString Identity;

		int32 SchemeVersion = 0;

		FString ContentRoot;

		FString JobId;

		bool bHasExtent = false;
		FMantlePlaceExtent Extent;
	};

	enum class EVerdict : uint8
	{
		NoPriorImport,

		SameIdentity,

		DifferentIdentity,

		LegacyUnrecorded,

		NewerScheme,
	};

	EVerdict Classify(
		bool bContentFolderExists,
		bool bRecordFound,
		const FRecord& Found,
		const FString& IncomingIdentity);

	FString ExplainRefusal(EVerdict Verdict, const FRecord& Found, const FString& ContentPath);

	FString ToJson(const FRecord& Record);
	bool FromJson(const FString& Json, FRecord& OutRecord);

	FString RecordPath(const FString& Identity);

	bool Read(const FString& Identity, FRecord& OutRecord);
	bool Write(const FRecord& Record);
}
