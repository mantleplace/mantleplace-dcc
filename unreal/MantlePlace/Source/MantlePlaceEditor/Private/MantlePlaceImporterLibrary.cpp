// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceImporterLibrary.h"

#include "MantlePlaceArtifactHandler.h"
#include "MantlePlaceBundleReaderLogic.h"
#include "MantlePlaceCesiumAvailabilityLogic.h"
#include "MantlePlaceDeliveryLogic.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceLevelFrameLogic.h"
#include "MantlePlaceManifestThreeLogic.h"
#include "MantlePlaceEditorSettings.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceImportStepsLogic.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceHLODLogic.h"
#include "MantlePlaceHLODs.h"
#include "MantlePlaceHandover.h"
#include "MantlePlaceLocalTileServer.h"
#include "MantlePlaceScopedRestore.h"
#include "MantlePlaceStreamStaging.h"
#include "MantlePlaceTerrainTilesLogic.h"
#include "MantlePlaceBundleCache.h"
#include "MantlePlaceSha256.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "Components/SplineComponent.h"
#include "Editor.h"
#include "Engine/DataTable.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "FileUtilities/ZipArchiveReader.h"
#include "DesktopPlatformModule.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "IDesktopPlatform.h"
#include "Framework/Application/SlateApplication.h"
#include "HAL/FileManager.h"
#include "HAL/IConsoleManager.h"
#include "HAL/PlatformProcess.h"
#include "GenericPlatform/GenericPlatformFile.h"
#include "HAL/PlatformFileManager.h"
#include "Landscape.h"
#include "Materials/MaterialInstanceConstant.h"
#include "Misc/FileHelper.h"
#include "Misc/Optional.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "ScopedTransaction.h"
#include "Settings/EditorLoadingSavingSettings.h"
#include "Subsystems/EditorAssetSubsystem.h"

#define LOCTEXT_NAMESPACE "MantlePlaceImporter"

namespace
{
	int32 ExtractSelectedEntries(
		const FZipArchiveReader& Reader, const MantlePlaceStreamStaging::FRecord& Selection,
		const FString& TempDir, int64& OutBytes)
	{
		int32 Count = 0;
		IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
		for (const FString& Name : Reader.GetFileNames())
		{
			if (!MantlePlaceStreamStaging::SelectsEntry(Selection, Name))
			{
				continue;
			}
			TArray<uint8> Bytes;
			if (!Reader.TryReadFile(Name, Bytes))
			{
				continue;
			}
			const FString OutPath = FPaths::Combine(TempDir, Name);
			FString Resolved = OutPath;
			FString Root = TempDir;
			FPaths::NormalizeFilename(Resolved);
			FPaths::NormalizeFilename(Root);
			FPaths::CollapseRelativeDirectories(Resolved);
			FPaths::CollapseRelativeDirectories(Root);
			if (!Root.EndsWith(TEXT("/")))
			{
				Root += TEXT("/");
			}
			if (!Resolved.StartsWith(Root, ESearchCase::IgnoreCase))
			{
				UE_LOG(LogMantlePlaceImport, Warning,
					TEXT("Skipped the archive entry %s: it resolves outside the staging directory."), *Name);
				continue;
			}
			PlatformFile.CreateDirectoryTree(*FPaths::GetPath(OutPath));
			if (FFileHelper::SaveArrayToFile(Bytes, *OutPath))
			{
				++Count;
				OutBytes += Bytes.Num();
			}
		}
		return Count;
	}

	bool RewriteCesiumTerrainAvailability(const FString& LayerJsonPath, const FString& JobId)
	{
		FString JsonText;
		if (!FFileHelper::LoadFileToString(JsonText, *LayerJsonPath))
		{
			return false;
		}
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<>> JsonReader = TJsonReaderFactory<>::Create(JsonText);
		if (!FJsonSerializer::Deserialize(JsonReader, Root) || !Root.IsValid())
		{
			return false;
		}

		const FString TerrainDir = FPaths::GetPath(LayerJsonPath);
		TArray<FString> TileFiles;
		IFileManager::Get().FindFilesRecursive(TileFiles, *TerrainDir, TEXT("*.terrain"), true, false);

		TArray<FMantlePlaceCesiumTile> Tiles;
		Tiles.Reserve(TileFiles.Num());
		for (const FString& TilePath : TileFiles)
		{
			FMantlePlaceCesiumTile Tile;
			if (FMantlePlaceCesiumAvailabilityLogic::ParseTilePath(TilePath, Tile))
			{
				Tiles.Add(Tile);
			}
		}
		if (Tiles.Num() == 0)
		{
			return false;
		}

		FMantlePlaceCesiumAvailabilityDiff Diff;
		if (!FMantlePlaceCesiumAvailabilityLogic::CorrectAvailability(Root, Tiles, Diff))
		{
			UE_LOG(LogMantlePlaceImport, Log,
				TEXT("Cesium terrain availability for job %s already lists exactly the %d tiles "
					 "present; no rewrite was needed. If this holds for every bundle, the "
					 "availability workaround can be removed."),
				*JobId, Tiles.Num());
			return false;
		}

		UE_LOG(LogMantlePlaceImport, Warning, TEXT("%s"),
			*FMantlePlaceCesiumAvailabilityLogic::DescribeMismatch(JobId, Diff, Tiles.Num()));

		FString OutText;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutText);
		if (!FJsonSerializer::Serialize(Root.ToSharedRef(), Writer))
		{
			return false;
		}
		return FFileHelper::SaveStringToFile(OutText, *LayerJsonPath);
	}

	TUniquePtr<FMantlePlaceLocalTileServer> GBundleStreamServer;
}

namespace MantlePlaceFormatThree
{
	bool IsFormatThree(const FZipArchiveReader& Reader)
	{
		FString Root, ManifestPath;
		return FMantlePlaceBundleReaderLogic::FindManifest(Reader.GetFileNames(), Root, ManifestPath)
			&& ManifestPath.Equals(FMantlePlaceBundleReaderLogic::ManifestPath3(), ESearchCase::IgnoreCase);
	}

	FMantlePlaceBundleRead ReadBundle(const FZipArchiveReader& Reader)
	{
		return FMantlePlaceBundleReaderLogic::Read(Reader.GetFileNames(),
			[&Reader](const FString& Name, FString& OutText)
			{
				TArray<uint8> Bytes;
				if (!Reader.TryReadFile(Name, Bytes))
				{
					return false;
				}
				FFileHelper::BufferToString(OutText, Bytes.GetData(), Bytes.Num());
				return true;
			});
	}

	bool Read(const FZipArchiveReader& Reader, TArray<uint8>& OutBytes, FMantlePlaceVaultManifest& OutManifest, FString& OutError)
	{
		const FMantlePlaceBundleRead BundleRead = ReadBundle(Reader);
		if (!BundleRead.IsRead())
		{
			OutError = BundleRead.Error;
			return false;
		}
		if (!Reader.TryReadFile(BundleRead.Root + BundleRead.ManifestPath, OutBytes))
		{
			OutError = FString::Printf(TEXT("Bundle has no readable %s."), *BundleRead.ManifestPath);
			return false;
		}
		FString Text;
		FFileHelper::BufferToString(Text, OutBytes.GetData(), OutBytes.Num());
		return FMantlePlaceManifestThreeLogic::ToVaultManifest(BundleRead, Text, OutManifest, OutError);
	}
}

bool UMantlePlaceImporterLibrary::FindListedFiles(const FString& ZipPath, const TArray<FString>& Names,
	TArray<FString>& OutListed, FString& OutError)
{
	OutListed.Reset();
	OutError.Reset();

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	IFileHandle* Handle = ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath) ? nullptr : PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		OutError = FString::Printf(TEXT("Could not open the download: %s"), *ZipPath);
		return false;
	}
	FZipArchiveReader Reader(Handle);
	if (!Reader.IsValid())
	{
		OutError = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return false;
	}
	const FMantlePlaceBundleRead BundleRead = MantlePlaceFormatThree::ReadBundle(Reader);
	if (!BundleRead.IsRead())
	{
		OutError = BundleRead.Error;
		return false;
	}
	OutListed = FMantlePlaceManifestThreeLogic::ListedAmong(BundleRead, Names);
	return true;
}

bool UMantlePlaceImporterLibrary::ReadManifestSha256(const FString& ZipPath, FString& OutSha256, FString& OutError)
{
	OutSha256.Reset();
	OutError.Reset();

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		OutError = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return false;
	}

	IFileHandle* Handle = PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		OutError = FString::Printf(TEXT("Could not open zip: %s"), *ZipPath);
		return false;
	}
	FZipArchiveReader Reader(Handle);
	if (!Reader.IsValid())
	{
		OutError = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return false;
	}

	TArray<uint8> ManifestBytes;
	if (MantlePlaceFormatThree::IsFormatThree(Reader))
	{
		FMantlePlaceVaultManifest Unused;
		if (!MantlePlaceFormatThree::Read(Reader, ManifestBytes, Unused, OutError) && ManifestBytes.Num() == 0)
		{
			return false;
		}
		OutError.Reset();
	}
	else if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
	{
		OutError = TEXT("Bundle has no Metadata/manifest.json.");
		return false;
	}

	OutSha256 = FMantlePlaceSha256::HexDigest(ManifestBytes);
	return true;
}

bool UMantlePlaceImporterLibrary::ReadVaultManifest(
	const FString& ZipPath, FMantlePlaceVaultManifest& OutManifest, FString& OutError)
{
	OutManifest = FMantlePlaceVaultManifest();
	OutError.Reset();

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		OutError = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return false;
	}

	IFileHandle* Handle = PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		OutError = FString::Printf(TEXT("Could not open zip: %s"), *ZipPath);
		return false;
	}
	FZipArchiveReader Reader(Handle);
	if (!Reader.IsValid())
	{
		OutError = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return false;
	}

	TArray<uint8> ManifestBytes;
	if (MantlePlaceFormatThree::IsFormatThree(Reader))
	{
		return MantlePlaceFormatThree::Read(Reader, ManifestBytes, OutManifest, OutError);
	}
	if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
	{
		OutError = TEXT("Bundle has no Metadata/manifest.json.");
		return false;
	}

	FString ManifestText;
	FFileHelper::BufferToString(ManifestText, ManifestBytes.GetData(), ManifestBytes.Num());

	OutManifest = MantlePlaceImportManifest::Parse(ManifestText, OutError);
	return true;
}

namespace
{
	struct FScopedNoContentBrowserSync
	{
		IConsoleVariable* const Var;
		const bool bPrevious;

		FScopedNoContentBrowserSync()
			: Var(IConsoleManager::Get().FindConsoleVariable(
				TEXT("Interchange.FeatureFlags.Import.SyncToBrowser")))
			, bPrevious(Var != nullptr && Var->GetBool())
		{
			if (Var != nullptr)
			{
				Var->Set(false, ECVF_SetByCode);
			}
		}

		~FScopedNoContentBrowserSync()
		{
			if (Var != nullptr)
			{
				Var->Set(bPrevious, ECVF_SetByCode);
			}
		}
	};
}

FMantlePlaceImportResult UMantlePlaceImporterLibrary::ImportVaultPackage(
	const FString& ZipPath,
	EMantlePlaceImportMode Mode)
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return ImportVaultPackageWithSelection(ZipPath,
		FMantlePlaceImportSelection::FromMode(Mode, Settings != nullptr && Settings->bGenerateFoliageScatter));
}

FMantlePlaceImportResult UMantlePlaceImporterLibrary::ImportVaultPackageWithSteps(
	const FString& ZipPath, EMantlePlaceImportMode Mode, FMantlePlaceImportSteps* Steps)
{
	const UMantlePlaceEditorSettings* Settings = GetDefault<UMantlePlaceEditorSettings>();
	return ImportVaultPackageWithSelection(ZipPath,
		FMantlePlaceImportSelection::FromMode(Mode, Settings != nullptr && Settings->bGenerateFoliageScatter), Steps);
}

FMantlePlaceImportResult UMantlePlaceImporterLibrary::ImportVaultPackageWithSelection(
	const FString& ZipPath,
	const FMantlePlaceImportSelection& Selection,
	FMantlePlaceImportSteps* Steps)
{
	FMantlePlaceImportResult Result;
	TArray<FString> Log;

	if (Steps != nullptr)
	{
		Steps->SetSynchronous(true);
	}
	const FMantlePlaceScopedRestore FinishSteps([Steps]()
	{
		if (Steps != nullptr)
		{
			Steps->Finish(FPlatformTime::Seconds());
		}
	});
	const FName IntegrityStep(TEXT("integrity_check"));

	MantlePlaceImportTiming::FTimeline Timeline;
	Timeline.Start();
	MantlePlaceImportTiming::FScopedCurrent PublishTimeline(Timeline);
	FMantlePlaceScopedRestore EmitTiming([&Timeline, &Result, &ZipPath]()
	{
		for (const FString& Line : Timeline.BuildSummary())
		{
			UE_LOG(LogMantlePlaceImport, Log, TEXT("%s"), *Line);
		}

		const FString RecordDirectory = MantlePlaceImportTiming::RecordDirectoryFromEnvironment();
		if (RecordDirectory.IsEmpty())
		{
			return;
		}

		MantlePlaceImportTiming::FRecordContext RecordContext;
		RecordContext.Bundle = FPaths::GetCleanFilename(ZipPath);
		RecordContext.bSucceeded = Result.bSuccess;

		const FString Written = MantlePlaceImportTiming::WriteRecord(Timeline, RecordContext, RecordDirectory);
		if (Written.IsEmpty())
		{
			UE_LOG(LogMantlePlaceImport, Warning,
			       TEXT("Could not write the import timeline record into %s (%s)"),
			       *RecordDirectory, MantlePlaceImportTiming::RecordDirectoryVariable);
		}
		else
		{
			UE_LOG(LogMantlePlaceImport, Log, TEXT("Import timeline record: %s"), *Written);
		}
	});

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		Result.Message = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return Result;
	}

	TOptional<MantlePlaceImportTiming::FScopedPhase> ZipPhase;
	ZipPhase.Emplace(Timeline, MantlePlaceImportTiming::Phase::ZipAndManifest);

	IFileHandle* Handle = PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		Result.Message = FString::Printf(TEXT("Could not open zip: %s"), *ZipPath);
		return Result;
	}
	FZipArchiveReader Reader(Handle);
	if (!Reader.IsValid())
	{
		Result.Message = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return Result;
	}

	TArray<uint8> ManifestBytes;
	FString ParseError;
	FMantlePlaceVaultManifest Manifest;
	if (MantlePlaceFormatThree::IsFormatThree(Reader))
	{
		if (!MantlePlaceFormatThree::Read(Reader, ManifestBytes, Manifest, ParseError))
		{
			Result.Message = FString::Printf(TEXT("Manifest error: %s"), *ParseError);
			return Result;
		}
	}
	else
	{
		if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
		{
			Result.Message = TEXT("Bundle has no Metadata/manifest.json.");
			return Result;
		}
		FString ManifestText;
		FFileHelper::BufferToString(ManifestText, ManifestBytes.GetData(), ManifestBytes.Num());
		Manifest = MantlePlaceImportManifest::Parse(ManifestText, ParseError);
	}
	Result.JobId = Manifest.JobId;
	if (!Manifest.bValid)
	{
		Result.Message = FString::Printf(TEXT("Manifest error: %s"), *ParseError);
		return Result;
	}

	Log.Add(FString::Printf(TEXT("Bundle manifest version %s (jobId %s)."),
		*Manifest.Version, *MantlePlaceImportNaming::ShortIdentity(Manifest.JobId)));

	Log.Append(FMantlePlaceDeliveryLogic::DescribeDelivery(Manifest.Delivery));

	ZipPhase.Reset();

	const MantlePlaceArtifacts::FHandlers Handlers = MantlePlaceArtifacts::MakeAll();
	if (Steps != nullptr)
	{
		Steps->Add(IntegrityStep, TEXT("Integrity Check"));
		MantlePlaceImportOrchestration::PlanSteps(Handlers, Manifest, Selection, *Steps);
	}
	Log.Append(MantlePlaceImportOrchestration::DescribeFormatSkips(Handlers, Manifest, Selection));

	const auto CancelledUntouched = [&Result, Steps]()
	{
		Steps->Finish(FPlatformTime::Seconds());
		Result.bCancelled = true;
		Result.Message = TEXT("Import cancelled before anything was imported. Nothing in the level or the project changed.");
		return Result;
	};

	if (Steps != nullptr && !Steps->Begin(IntegrityStep, FPlatformTime::Seconds()))
	{
		return CancelledUntouched();
	}
	FString IntegrityError;
	if (!MantlePlaceImportOrchestration::VerifyAll(Manifest, Reader, Timeline, IntegrityError, &Log))
	{
		Result.Message = FString::Printf(TEXT("Integrity check failed: %s"), *IntegrityError);
		return Result;
	}
	if (Steps != nullptr)
	{
		Steps->End(IntegrityStep, true, FPlatformTime::Seconds());
		if (Steps->IsCancelRequested())
		{
			return CancelledUntouched();
		}
	}

	UWorld* World = GEditor ? GEditor->GetEditorWorldContext().World() : nullptr;
	if (World == nullptr)
	{
		Result.Message = TEXT("No editor world is open to import into.");
		return Result;
	}

	const FString Identity = MantlePlaceImportNaming::ResolveIdentity(
		Manifest.OrderId, FMantlePlaceSha256::HexDigest(ManifestBytes));
	if (!MantlePlaceImportNaming::IsUsableIdentity(Identity))
	{
		Result.Message = TEXT(
			"This bundle carries neither a usable order id nor a hashable manifest, so there is no "
			"safe name for its content folder. Nothing has been imported.");
		return Result;
	}

	const FString ContentRoot = UMantlePlaceEditorSettings::ResolveGeneratedContentRoot();
	const FString DestPackagePath = MantlePlaceImportNaming::ImportRoot(ContentRoot, Identity);
	Result.Identity = Identity;
	Result.ContentPath = DestPackagePath;

	const FString NothingToImport =
		MantlePlaceImportOrchestration::ExplainNothingToImport(Handlers, Manifest, Selection);
	if (!NothingToImport.IsEmpty())
	{
		Result.Message = NothingToImport;
		return Result;
	}

	const FString TempDir = FPaths::ProjectSavedDir() / TEXT("MantlePlace") / TEXT("ImportTmp")
		/ MantlePlaceImportNaming::ShortIdentity(Identity);
	PlatformFile.CreateDirectoryTree(*TempDir);

	const FScopedNoContentBrowserSync NoBrowserSync;

	UEditorLoadingSavingSettings* LoadSaveSettings = GetMutableDefault<UEditorLoadingSavingSettings>();
	LoadSaveSettings->SetAutomaticallyCheckoutOnAssetModificationOverride(false);
	const FMantlePlaceScopedRestore RestoreAutoCheckout(
		[LoadSaveSettings] { LoadSaveSettings->ResetAutomaticallyCheckoutOnAssetModificationOverride(); });

	MantlePlaceImportOrchestration::FPriorActorLoad PriorActorLoad;
	const FString UnloadedRefusal = MantlePlaceImportOrchestration::LoadUnloadedPriorActors(World, Identity, PriorActorLoad);
	if (!UnloadedRefusal.IsEmpty())
	{
		Result.Message = UnloadedRefusal;
		return Result;
	}
	if (PriorActorLoad.Loaded > 0)
	{
		Log.Add(FString::Printf(
			TEXT("Loaded %d actor(s) of the previous import from unloaded World Partition regions, to replace them."),
			PriorActorLoad.Loaded));
	}

	FMantlePlaceFrameOrigin FrameOrigin;
	FrameOrigin.Crs = Manifest.Georef.CrsProjected;
	FrameOrigin.VerticalDatum = Manifest.SiteVerticalDatum;
	FrameOrigin.EastingM = Manifest.Georef.OriginEastingM;
	FrameOrigin.NorthingM = Manifest.Georef.OriginNorthingM;
	FrameOrigin.bStated = Manifest.Georef.bHasOrigin;
	const FMantlePlaceFramePlan FramePlan = FMantlePlaceLevelFrameLogic::Plan(
		Identity, FrameOrigin, MantlePlaceImportOrchestration::ReadLevelImports(World));
	Manifest.SetLevelOffsetM(FramePlan.OffsetEastM, FramePlan.OffsetNorthM);
	if (!FramePlan.Warning.IsEmpty())
	{
		Log.Add(FramePlan.Warning);
		UE_LOG(LogMantlePlaceImport, Warning, TEXT("%s"), *FramePlan.Warning);
	}
	if (!FramePlan.Note.IsEmpty())
	{
		Log.Add(FramePlan.Note);
	}
	if (FramePlan.HasOffset())
	{
		const FVector OffsetCm = FMantlePlaceVaultManifest::ProjectedToUeCm(FramePlan.OffsetEastM, FramePlan.OffsetNorthM, 0.0);
		Log.Add(FString::Printf(
			TEXT("Placed beside the other import(s) in the level: %.1f m north and %.1f m east of the level's first import."),
			OffsetCm.X / 100.0, OffsetCm.Y / 100.0));
	}

	const FString Refusal = MantlePlaceImportOrchestration::WipePriorAssets(Identity, DestPackagePath, Timeline);
	if (!Refusal.IsEmpty())
	{
		Result.Message = Refusal;
		return Result;
	}

	FMantlePlaceImportContext Context(World, Manifest, &Reader, Selection, Timeline, Result, Log);
	Context.Identity = Identity;
	Context.FrameTag = FramePlan.FrameValue;
	Context.TempDir = TempDir;
	Context.DestPackagePath = DestPackagePath;
	Context.ContentRoot = ContentRoot;
	Context.Steps = Steps;

	MantlePlaceImportOrchestration::RunPrepare(Handlers, Context);

	FScopedTransaction Transaction(LOCTEXT("ImportVaultPackage", "Import Mantle Place Vault Package"));

	const MantlePlaceImportTiming::FScopedPhase TransactionPhase(
		Timeline, MantlePlaceImportTiming::Phase::InsideTransaction);

	TArray<TWeakObjectPtr<AActor>> KeptPartitionActors;
	const int32 Replaced = MantlePlaceImportOrchestration::ReplacePriorActors(World, Identity, Timeline, &KeptPartitionActors);
	if (Replaced > 0)
	{
		Log.Add(FString::Printf(TEXT("Replaced %d actor(s) from a prior import of this bundle."), Replaced));
	}

	MantlePlaceImportOrchestration::RunImport(Handlers, Context);
	MantlePlaceImportOrchestration::RunFinalize(Handlers, Context);
	MantlePlaceImportOrchestration::ReleaseKeptPartitionActors(World, KeptPartitionActors);

	const FString HLODNote = MantlePlaceHLODLogic::PostImportNote(
		UWorld::IsPartitionedWorld(World), MantlePlaceHLODs::CountAssigned(World, ContentRoot));
	if (!HLODNote.IsEmpty())
	{
		Log.Add(HLODNote);
	}

	if (Steps != nullptr)
	{
		Steps->Finish(FPlatformTime::Seconds());
		Result.bCancelled = Steps->WasCancelled();
	}
	Result.bSuccess = !Result.bCancelled && Context.bAllRequestedSucceeded && Result.CreatedActors.Num() > 0;
	if (Result.bCancelled)
	{
		Log.Add(Steps->DescribeCancel());
		UE_LOG(LogMantlePlaceImport, Log, TEXT("%s"), *Log.Last());
	}
	if (Result.bSuccess || (Result.bCancelled && Result.CreatedActors.Num() > 0))
	{
		Log.Add(TEXT("Nothing is saved yet: the imported assets and your level stay unsaved until you use File > Save All (Ctrl+Shift+S)."));
		UE_LOG(LogMantlePlaceImport, Log,
			TEXT("Import complete: %d actor(s) created. Nothing is saved yet: the imported assets and "
				 "your level stay unsaved until you use File > Save All (Ctrl+Shift+S)."),
			Result.CreatedActors.Num());
	}
	Result.Message = FString::Join(Log, TEXT("\n"));

	if (Result.bSuccess || Result.bCancelled)
	{
		MantlePlaceImportProvenance::FRecord Record;
		Record.Identity = Identity;
		Record.SchemeVersion = MantlePlaceImportProvenance::CurrentSchemeVersion;
		Record.ContentRoot = ContentRoot;
		Record.JobId = Manifest.JobId;
		if (Manifest.Cesium.bHasBbox)
		{
			Record.Extent.West = Manifest.Cesium.BboxWestDeg;
			Record.Extent.South = Manifest.Cesium.BboxSouthDeg;
			Record.Extent.East = Manifest.Cesium.BboxEastDeg;
			Record.Extent.North = Manifest.Cesium.BboxNorthDeg;
			Record.bHasExtent = Record.Extent.IsValid();
		}
		if (!MantlePlaceImportProvenance::Write(Record))
		{
			UE_LOG(LogMantlePlaceImport, Warning,
				TEXT("Import succeeded but its provenance record could not be written to %s. A "
					 "future re-import of this order will refuse rather than replace it."),
				*MantlePlaceImportProvenance::RecordPath(Identity));
		}
	}
	return Result;
}

bool UMantlePlaceImporterLibrary::BrowseForVaultZip(FString& OutZipPath)
{
	IDesktopPlatform* DesktopPlatform = FDesktopPlatformModule::Get();
	if (DesktopPlatform == nullptr)
	{
		return false;
	}

	const void* ParentWindowHandle = FSlateApplication::Get().FindBestParentWindowHandleForDialogs(nullptr);

	const FString DefaultPath = FPlatformProcess::UserDir();

	TArray<FString> OutFiles;
	const bool bPicked = DesktopPlatform->OpenFileDialog(
		ParentWindowHandle,
		TEXT("Select Mantle Place vault bundle"),
		DefaultPath,
		TEXT(""),
		TEXT("Vault bundle (*.zip)|*.zip"),
		EFileDialogFlags::None,
		OutFiles);

	if (bPicked && OutFiles.Num() > 0)
	{
		OutZipPath = FPaths::ConvertRelativePathToFull(OutFiles[0]);
		return true;
	}
	return false;
}

FMantlePlaceStreamInfo UMantlePlaceImporterLibrary::StreamBundleIntoCesium(const FString& ZipPath)
{
	FMantlePlaceStreamInfo Info;

	IPlatformFile& PlatformFile = FPlatformFileManager::Get().GetPlatformFile();
	if (ZipPath.IsEmpty() || !PlatformFile.FileExists(*ZipPath))
	{
		Info.Message = FString::Printf(TEXT("Zip not found: %s"), *ZipPath);
		return Info;
	}

	IFileHandle* Handle = PlatformFile.OpenRead(*ZipPath);
	if (Handle == nullptr)
	{
		Info.Message = FString::Printf(TEXT("Could not open zip: %s"), *ZipPath);
		return Info;
	}
	FZipArchiveReader Reader(Handle);
	if (!Reader.IsValid())
	{
		Info.Message = FString::Printf(TEXT("Not a readable zip archive: %s"), *ZipPath);
		return Info;
	}

	TArray<uint8> ManifestBytes;
	FMantlePlaceVaultManifest Manifest;
	const FZipArchiveReader* TerrainSource = &Reader;
	TUniquePtr<FZipArchiveReader> TilesReader;
	FString TerrainPrefix;
	FString TerrainPath;
	FString SourceDigest;
	int32 TerrainTileCount = 0;
	bool bFormatThree = false;

	if (MantlePlaceFormatThree::IsFormatThree(Reader))
	{
		bFormatThree = true;
		FString ReadError;
		if (!MantlePlaceFormatThree::Read(Reader, ManifestBytes, Manifest, ReadError))
		{
			Info.Message = ReadError;
			return Info;
		}
		Info.JobId = Manifest.JobId;

		const FString TilesPath = FMantlePlaceTerrainTilesLogic::TilesZipBeside(ZipPath);
		FMantlePlaceTerrainTilesLogic::FTileset Tileset;
		if (PlatformFile.FileExists(*TilesPath))
		{
			if (IFileHandle* TilesHandle = PlatformFile.OpenRead(*TilesPath))
			{
				TilesReader = MakeUnique<FZipArchiveReader>(TilesHandle);
				if (TilesReader->IsValid())
				{
					Tileset = FMantlePlaceTerrainTilesLogic::FindTileset(TilesReader->GetFileNames());
				}
			}
		}
		if (FMantlePlaceTerrainTilesLogic::DecideSource(true, Tileset.bFound)
			== FMantlePlaceTerrainTilesLogic::ESource::ExtraMissing)
		{
			const FString CacheRoot = FPaths::Combine(FPaths::ProjectSavedDir(), GetDefault<UMantlePlaceBundleCache>()->CacheSubDir);
			Info.bTerrainTilesFetchable = FMantlePlaceTerrainTilesLogic::IsInVaultCache(ZipPath, CacheRoot);
			Info.Message = Info.bTerrainTilesFetchable ? FMantlePlaceTerrainTilesLogic::NotFetchedYetMessage()
				: FMantlePlaceTerrainTilesLogic::NoTilesBesideReason();
			return Info;
		}

		TArray<uint8> TilesBytes;
		if (!FFileHelper::LoadFileToArray(TilesBytes, *TilesPath))
		{
			Info.Message = FString::Printf(TEXT("Could not read the terrain tiles beside the bundle: %s"), *TilesPath);
			return Info;
		}
		TerrainSource = TilesReader.Get();
		TerrainPrefix = Tileset.Prefix;
		TerrainPath = Tileset.LayerJsonPath;
		TerrainTileCount = Tileset.TileCount;
		SourceDigest = FMantlePlaceSha256::HexDigest(TilesBytes);
		if (Manifest.Georef.bHasGeographicOrigin)
		{
			Info.bHasOrigin = true;
			Info.OriginLongitudeDeg = Manifest.Georef.OriginLongitudeDeg;
			Info.OriginLatitudeDeg = Manifest.Georef.OriginLatitudeDeg;
		}
	}
	else
	{
		if (!Reader.TryReadFile(TEXT("Metadata/manifest.json"), ManifestBytes))
		{
			Info.Message = TEXT("Bundle has no Metadata/manifest.json.");
			return Info;
		}
		FString ManifestText;
		FFileHelper::BufferToString(ManifestText, ManifestBytes.GetData(), ManifestBytes.Num());
		FString ParseError;
		Manifest = MantlePlaceImportManifest::Parse(ManifestText, ParseError);
		Info.JobId = Manifest.JobId;

		if (!Manifest.Cesium.bHasTerrain)
		{
			Info.Message = TEXT("This bundle ships no Cesium quantized-mesh terrain (layout.cesiumTerrain is "
				"absent). Re-export from a v8+ pipeline that produces Cesium terrain tiles.");
			return Info;
		}

		TerrainPrefix = MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(Manifest.Cesium.TerrainPath);
		if (TerrainPrefix.IsEmpty())
		{
			Info.Message = TEXT("Bundle's layout.cesiumTerrain has no directory component; refusing to "
				"extract (would match every zip entry).");
			return Info;
		}
		TerrainPath = Manifest.Cesium.TerrainPath;
		TerrainTileCount = Manifest.Cesium.TerrainTileCount;
		SourceDigest = FMantlePlaceSha256::HexDigest(ManifestBytes);
	}

	const FString ManifestSha256 = SourceDigest;
	const FString Identity =
		MantlePlaceImportNaming::ResolveIdentity(Manifest.OrderId, ManifestSha256);
	if (!MantlePlaceImportNaming::IsUsableIdentity(Identity))
	{
		Info.Message = TEXT("Bundle carries neither a usable order id nor a readable manifest digest, "
			"so there is no safe name for its staging directory. Nothing was extracted.");
		return Info;
	}

	const FString StageDir = MantlePlaceStreamStaging::StagingDir(Identity);
	const FString TerrainRootOnDisk = FPaths::Combine(StageDir, TerrainPath);

	MantlePlaceStreamStaging::FRecord Incoming;
	Incoming.Identity = Identity;
	Incoming.ManifestSha256 = ManifestSha256;
	Incoming.TerrainPrefix = TerrainPrefix;
	Incoming.CesiumTerrainPath = TerrainPath;
	Incoming.DrapePath = (!bFormatThree && Manifest.Drape.bPresent && !Manifest.Drape.Path.IsEmpty()) ? Manifest.Drape.Path : FString();
	Incoming.SchemeVersion = MantlePlaceStreamStaging::CurrentSchemeVersion;

	MantlePlaceStreamStaging::FRecord Staged;
	const bool bHasRecord = MantlePlaceStreamStaging::Read(Identity, Staged);
	const MantlePlaceStreamStaging::EVerdict Verdict = MantlePlaceStreamStaging::Classify(
		bHasRecord, Staged, Incoming, PlatformFile.FileExists(*TerrainRootOnDisk));

	if (Verdict == MantlePlaceStreamStaging::EVerdict::Stage)
	{
		PlatformFile.DeleteDirectoryRecursively(*StageDir);
		PlatformFile.CreateDirectoryTree(*StageDir);

		int64 StagedBytes = 0;
		Incoming.EntryCount = ExtractSelectedEntries(*TerrainSource, Incoming, StageDir, StagedBytes);
		if (Incoming.EntryCount == 0)
		{
			Info.Message = FString::Printf(TEXT("Bundle declares Cesium terrain but no %s entries were extracted."), *TerrainPrefix);
			return Info;
		}
		UE_LOG(LogMantlePlaceImport, Log,
			TEXT("Staged %d entries (%lld bytes) for this bundle at %s."),
			Incoming.EntryCount, StagedBytes, *StageDir);

		{
			const MantlePlaceImportTiming::FScopedCurrentPhase AvailabilityPhase(
				MantlePlaceImportTiming::Phase::CesiumAvailability,
				FString::Printf(TEXT("%d entries"), Incoming.EntryCount));
			RewriteCesiumTerrainAvailability(TerrainRootOnDisk, Manifest.JobId);
		}

		if (!MantlePlaceStreamStaging::Write(Incoming))
		{
			UE_LOG(LogMantlePlaceImport, Warning,
				TEXT("Streaming staged correctly but its record could not be written to %s; the next "
					 "stream of this bundle will extract it again."),
				*MantlePlaceStreamStaging::RecordPath(Identity));
		}
	}
	else
	{
		UE_LOG(LogMantlePlaceImport, Log,
			TEXT("Reusing the %d entries already staged for this bundle at %s."),
			Staged.EntryCount, *StageDir);
	}

	if (!GBundleStreamServer.IsValid())
	{
		GBundleStreamServer = MakeUnique<FMantlePlaceLocalTileServer>();
	}
	int32 FirstPort = 0;
	int32 PortCount = 0;
	UMantlePlaceEditorSettings::ResolveLocalTileServerPortScan(FirstPort, PortCount);

	FString BaseUrl, ServerError;
	for (int32 Offset = 0; Offset < PortCount && BaseUrl.IsEmpty(); ++Offset)
	{
		BaseUrl = GBundleStreamServer->Start(StageDir, static_cast<uint32>(FirstPort + Offset), ServerError);
	}
	if (BaseUrl.IsEmpty())
	{
		Info.Message = FString::Printf(
			TEXT("Failed to start local tile server on any port from %d to %d: %s. Change the first "
				 "port or the scan count under Project Settings -> Plugins -> Mantle Place."),
			FirstPort, FirstPort + PortCount - 1, *ServerError);
		return Info;
	}

	Info.bSuccess = true;
	Info.BaseUrl = BaseUrl;
	Info.CesiumTerrainUrl = BaseUrl / TerrainPath;
	if (!Incoming.DrapePath.IsEmpty())
	{
		Info.ImageryUrl = BaseUrl / Incoming.DrapePath;
	}
	Info.bHasBbox = Manifest.Cesium.bHasBbox;
	Info.BboxWestDeg = Manifest.Cesium.BboxWestDeg;
	Info.BboxSouthDeg = Manifest.Cesium.BboxSouthDeg;
	Info.BboxEastDeg = Manifest.Cesium.BboxEastDeg;
	Info.BboxNorthDeg = Manifest.Cesium.BboxNorthDeg;
	Info.Message = FString::Printf(
		TEXT("Streaming bundle %s on %s (%d Cesium terrain tiles). Cesium3DTileset Url -> %s"),
		*MantlePlaceImportNaming::ShortIdentity(Manifest.JobId), *BaseUrl, TerrainTileCount, *Info.CesiumTerrainUrl);
	return Info;
}

void UMantlePlaceImporterLibrary::StopBundleStream()
{
	if (GBundleStreamServer.IsValid())
	{
		GBundleStreamServer->Stop();
	}
}

FString UMantlePlaceImporterLibrary::BuildHLODs()
{
	UWorld* World = GEditor != nullptr ? GEditor->GetEditorWorldContext().World() : nullptr;
	return MantlePlaceHLODs::Build(World).Message;
}

FString UMantlePlaceImporterLibrary::PrepareForHandover()
{
	return MantlePlaceHandover::PrepareEditorWorld().Summary();
}

#undef LOCTEXT_NAMESPACE
