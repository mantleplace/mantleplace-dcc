// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "MantlePlaceVaultTypes.h"

class FJsonObject;

struct FMantlePlaceVaultLogic
{
	static FString NormalizeBaseUrl(const FString& BaseUrl);

	static FString BuildListBundlesUrl(const FString& BaseUrl);

	static FString BuildDownloadUrl(const FString& BaseUrl, const FString& OrderId);

	static FString BuildDownloadBody(const FString& Format);

	static bool ParseListResponse(const FString& JsonStr, TArray<FMantlePlaceVaultItem>& OutItems, FString& OutError, TArray<FString>* OutWarnings = nullptr);

	static bool ParseDownloadResponse(const FString& JsonStr, FMantlePlacePresignedDownload& OutDownload, FString& OutError);

	static bool ParseErrorBody(const FString& JsonStr, FString& OutError, FString& OutCode);

	static bool IsExtraLockedRefusal(int32 ResponseCode, const FString& JsonStr);

	static EMantlePlaceVaultBundleStatus ParseStatus(const FString& Status);

	static bool IsDownloadable(const FMantlePlaceVaultItem& Item);

	static bool IsKnownFormat(const FString& Format);

	static const TArray<FString>& KnownFormats();

	static const FString& WholeBundleFormat();

	static bool IsPresignableFormat(const FString& Format);

	static FString BuildMaterializeUrl(const FString& BaseUrl, const FString& OrderId);

	static bool IsValidMaterializeScope(const FString& Scope);

	static const TArray<FString>& TargetedImportTokens();

	static FString BuildMaterializeBody(const FString& Scope);

	static FString BuildMaterializeBodyForTokens(const TArray<FString>& Tokens);

	static bool ParseMaterializeStartResponse(const FString& JsonStr, FMantlePlaceMaterializeStart& OutStart, FString& OutError);

	static bool IsPermanentlyAbsentReason(const FString& Reason);

	static TArray<FString> RequestedForPolling(const FMantlePlaceMaterializeStart& Start);

	static EMantlePlaceMaterializeState ParseMaterializeState(const FString& State);

	static bool ParseMaterializeStatus(const FString& JsonStr, const TArray<FString>& Requested, FMantlePlaceMaterializeStatus& OutStatus, FString& OutError);

	static bool ParseMaterializeStatus(const FString& JsonStr, FMantlePlaceMaterializeStatus& OutStatus, FString& OutError);

	static void DeriveMaterializeDelivery(const TSharedPtr<FJsonObject>& Root, const TArray<FString>& Requested, FMantlePlaceMaterializeStatus& OutStatus);

	static bool IsDeliveryStateUnknown(int32 HttpStatus, const FString& Body);

	static const FString& DeliveryStateUnknownCode();

	static EMantlePlaceImportRoute DecideImportRoute(const FMantlePlaceMaterializeStart& Start);

	static const FString& ViewHostKey();

	static const FString& ViewEveryEntryLevel();

	static FString BuildViewDownloadBody();

	static EMantlePlaceViewFetch DecideViewFetch(
		const FMantlePlacePresignedDownload& Download, bool bDeliveryKnown, const TArray<FString>& Delivered);

	static const TArray<FString>& OfferedLevels();

	static bool IsOfferedLevel(const FString& Level);

	static void ReadLevelOffer(const TSharedPtr<FJsonObject>& Row, FMantlePlaceVaultItem& Item, FString& OutWarning);

	static FString BuildExtraDownloadBody(const FString& ExtraId);

	static FString BuildLevelDownloadBody(const FString& Level, const TArray<FString>& Files);

	static bool ParseLevelDownloadResponse(const FString& JsonStr, const FString& Level, FMantlePlacePresignedDownload& OutDownload, FString& OutError);

	static bool FilesLeftAfterNotInBundle(int32 ResponseCode, const FString& JsonStr, const TArray<FString>& Asked,
		TArray<FString>& OutRemaining, TArray<FString>& OutAbsent);

	static const TArray<FString>& ReadOutsideHostBlock();

	static TArray<FString> MissingFromView(
		const TArray<FString>& Parts, bool bDeliveryKnown, const TArray<FString>& Delivered);

	static bool IsIncompleteBundle(const FMantlePlaceVaultItem& Item);

	static FString DeriveTierLabel(const FMantlePlaceVaultItem& Item);

	static bool ShouldRecoverMissingUnrealPayload(
		bool bManifestReadable, bool bManifestValid, const FString& OrderId, bool bAlreadyRecovered);
};
