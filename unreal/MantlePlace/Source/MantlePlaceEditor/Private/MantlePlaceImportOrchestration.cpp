// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceImportOrchestration.h"

#include "MantlePlaceDataLayers.h"
#include "MantlePlaceFoliageScatter.h"
#include "MantlePlaceFoliageVolume.h"
#include "MantlePlaceHLODLogic.h"
#include "MantlePlaceHLODs.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceImportStepsLogic.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceIntegrityLogic.h"
#include "MantlePlaceOdblLogic.h"
#include "MantlePlaceSha256.h"
#include "MantlePlaceWorldPartitionLogic.h"

#include "Components/BoxComponent.h"
#include "Editor.h"
#include "EngineUtils.h"
#include "FileUtilities/ZipArchiveReader.h"
#include "GameFramework/Actor.h"
#include "Grid/PCGPartitionActor.h"
#include "Landscape.h"
#include "LandscapeInfo.h"
#include "LandscapeStreamingProxy.h"
#include "HAL/PlatformTime.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Subsystems/EditorAssetSubsystem.h"
#include "UObject/Package.h"
#include "WorldPartition/ActorPartition/PartitionActorDesc.h"
#include "WorldPartition/WorldPartition.h"
#include "WorldPartition/WorldPartitionActorDescInstance.h"
#include "WorldPartition/WorldPartitionHelpers.h"

DEFINE_LOG_CATEGORY(LogMantlePlaceImport);

bool FMantlePlaceImportContext::ReadBytes(const FString& InZipPath, TArray<uint8>& OutBytes) const
{
	return Reader != nullptr && Reader->TryReadFile(InZipPath, OutBytes);
}

bool FMantlePlaceImportContext::Extract(const FString& InZipPath, FString& OutDiskPath, FString& OutError) const
{
	TArray<uint8> Bytes;
	if (!ReadBytes(InZipPath, Bytes))
	{
		OutError = FString::Printf(TEXT("Bundle is missing expected entry: %s"), *InZipPath);
		return false;
	}

	OutDiskPath = TempDir / FPaths::GetCleanFilename(InZipPath);
	if (!FFileHelper::SaveArrayToFile(Bytes, *OutDiskPath))
	{
		OutError = FString::Printf(TEXT("Could not write temp file: %s"), *OutDiskPath);
		return false;
	}
	return true;
}

void FMantlePlaceImportContext::Claim(AActor* Actor, const FString& Label) const
{
	if (Actor == nullptr)
	{
		return;
	}
	const FName Tag(*MantlePlaceImportNaming::ImportTag(Identity));

	const bool bOdbl = MantlePlaceOdblLogic::RoleOfPaths(Manifest.FileLicensing, SourcePaths) == EMantlePlaceOdblRole::Database;
	const FName Folder(bOdbl ? *MantlePlaceImportNaming::OdblFolder(Identity) : *MantlePlaceImportNaming::OutlinerFolder(Identity));
	TArray<FName> ExtraTags;
	if (bOdbl)
	{
		ExtraTags.Add(MantlePlaceImportNaming::OdblTag());
	}
	if (!EquivalenceGroup.IsNone())
	{
		ExtraTags.Add(FName(*MantlePlaceImportNaming::EquivalenceTag(EquivalenceGroup.ToString())));
	}
	if (MantlePlaceOdblLogic::NeedsCredit(Manifest.FileLicensing, SourcePaths))
	{
		for (const FString& CreditTag : MantlePlaceOdblLogic::CreditTags(
				 MantlePlaceOdblLogic::CreditLines(Manifest.Attribution, Manifest.FileLicensing, SourcePaths)))
		{
			ExtraTags.Add(FName(*CreditTag));
		}
	}
	const auto Mark = [&](AActor* Marked)
	{
		Marked->Tags.AddUnique(Tag);
		if (!FrameTag.IsEmpty())
		{
			Marked->Tags.AddUnique(FName(*MantlePlaceImportNaming::FrameTag(FrameTag)));
		}
		for (const FName& Extra : ExtraTags)
		{
			Marked->Tags.AddUnique(Extra);
		}
		Marked->SetFolderPath(Folder);
	};

	Actor->SetActorLabel(Label);
	Mark(Actor);

	if (!UWorld::IsPartitionedWorld(Actor->GetWorld()))
	{
		return;
	}

	MantlePlaceDataLayers::Assign(Actor, ContentRoot, Family);
	if (bOdbl)
	{
		MantlePlaceDataLayers::AssignOdbl(Actor, ContentRoot);
	}

	MantlePlaceHLODs::Assign(Actor, ContentRoot, Family);

	const bool bStreamedFamily = Actor->IsA<ALandscapeProxy>() || MantlePlaceHLODLogic::HasHLOD(Family);
	if (MantlePlaceWorldPartitionLogic::ShouldForceNonSpatiallyLoaded(true, bStreamedFamily)
		&& Actor->CanChangeIsSpatiallyLoadedFlag())
	{
		Actor->SetIsSpatiallyLoaded(false);
	}

	if (const ALandscape* Landscape = Cast<ALandscape>(Actor))
	{
		if (const ULandscapeInfo* Info = Landscape->GetLandscapeInfo())
		{
			for (const TWeakObjectPtr<ALandscapeStreamingProxy>& Proxy : Info->GetSortedStreamingProxies())
			{
				if (Proxy.IsValid())
				{
					Mark(Proxy.Get());
					MantlePlaceDataLayers::Assign(Proxy.Get(), ContentRoot, Family);
					if (bOdbl)
					{
						MantlePlaceDataLayers::AssignOdbl(Proxy.Get(), ContentRoot);
					}
					MantlePlaceHLODs::Assign(Proxy.Get(), ContentRoot, Family);
				}
			}
		}
	}
}

namespace
{
struct FIntegrityTiming
{
	explicit FIntegrityTiming(MantlePlaceImportTiming::FTimeline& Timeline)
		: Read(Timeline, MantlePlaceImportTiming::Phase::IntegrityEntryRead)
		, Digest(Timeline, MantlePlaceImportTiming::Phase::IntegrityDigest)
	{
	}

	void Record()
	{
		Read.Record();
		Digest.Record();
	}

	MantlePlaceImportTiming::FAccumulatedPhase Read;
	MantlePlaceImportTiming::FAccumulatedPhase Digest;
};

bool VerifyEntrySha256(
	const FZipArchiveReader& Reader,
	const FString& InZipPath,
	const FString& ExpectedHex,
	FIntegrityTiming& Timing,
	FString& OutError)
{
	if (ExpectedHex.IsEmpty())
	{
		return true;
	}
	TArray<uint8> Bytes;
	{
		const MantlePlaceImportTiming::FAccumulatedPhase::FSpan ReadSpan(Timing.Read);
		if (!Reader.TryReadFile(InZipPath, Bytes))
		{
			OutError = FString::Printf(TEXT("Integrity check could not read entry: %s"), *InZipPath);
			return false;
		}
	}
	FString Actual;
	{
		const MantlePlaceImportTiming::FAccumulatedPhase::FSpan DigestSpan(Timing.Digest);
		Actual = FMantlePlaceSha256::HexDigest(Bytes);
	}
	if (!Actual.Equals(ExpectedHex, ESearchCase::IgnoreCase))
	{
		OutError = FString::Printf(
			TEXT("Integrity check failed for %s: manifest sha256 %s != computed %s."),
			*InZipPath, *ExpectedHex, *Actual);
		return false;
	}
	return true;
}

MantlePlaceWorldPartitionLogic::FDescriptorPackage ReadDescriptorPackage(const FWorldPartitionActorDescInstance& Desc)
{
	MantlePlaceWorldPartitionLogic::FDescriptorPackage State;
	UPackage* Package = FindPackage(nullptr, *Desc.GetActorPackage().ToString());
	if (Package == nullptr)
	{
		return State;
	}
	State.bInMemory = true;
	State.bDirty = Package->IsDirty();
	State.bHoldsLiveActor = AActor::FindActorInPackage(Package, false) != nullptr;
	return State;
}
}

bool MantlePlaceImportOrchestration::VerifyAll(
	const FMantlePlaceVaultManifest& Manifest, const FZipArchiveReader& Reader,
	MantlePlaceImportTiming::FTimeline& Timeline, FString& OutError, TArray<FString>* OutNotes)
{
	const MantlePlaceImportTiming::FScopedPhase IntegrityPhase(
		Timeline, MantlePlaceImportTiming::Phase::IntegrityPrecheck);

	FIntegrityTiming IntegrityTiming(Timeline);

	const TArray<FMantlePlaceDeclaredArtifact> Declared =
		FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(Manifest);
	int32 VerifiedCount = 0;
	int32 UnverifiableCount = 0;
	int32 LeftOutCount = 0;
	for (const FMantlePlaceDeclaredArtifact& Artifact : Declared)
	{
		if (FMantlePlaceIntegrityLogic::LeavesOut(Manifest, Artifact.Path))
		{
			++LeftOutCount;
			const FString Note = FString::Printf(
				TEXT("Not in this archive (left out of the selection), skipped: %s."), *Artifact.Path);
			UE_LOG(LogMantlePlaceImport, Log, TEXT("%s"), *Note);
			if (OutNotes != nullptr)
			{
				OutNotes->Add(Note);
			}
			continue;
		}

		FString IntegrityError;
		if (!VerifyEntrySha256(Reader, Artifact.Path, Artifact.Sha256, IntegrityTiming, IntegrityError))
		{
			IntegrityTiming.Record();
			OutError = IntegrityError;
			return false;
		}
		if (Artifact.IsVerifiable())
		{
			++VerifiedCount;
		}
		else
		{
			++UnverifiableCount;
		}
	}

	IntegrityTiming.Record();

	UE_LOG(LogMantlePlaceImport, Log,
		TEXT("Integrity verified: %d of %d declared payload(s) matched their manifest sha256, "
			 "%d carried no digest and are valid but unverified, %d not in this archive (jobId %s)."),
		VerifiedCount, Declared.Num(), UnverifiableCount, LeftOutCount,
		*MantlePlaceImportNaming::ShortIdentity(Manifest.JobId));
	return true;
}

FString MantlePlaceImportOrchestration::ExplainNothingToImport(
	const FHandlers& Handlers, const FMantlePlaceVaultManifest& Manifest,
	const FMantlePlaceImportSelection& Selection)
{
	bool bRequested = false;
	bool bCanProduce = false;
	TArray<FString> Reasons;
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (!Handler->IsTerrainRepresentation() || !Selection.Includes(Handler->Id()))
		{
			continue;
		}
		bRequested = true;
		TArray<FString> Paths;
		Handler->CollectSourcePaths(Manifest, Paths);
		if (Handler->IsPresent(Manifest) && FMantlePlaceIntegrityLogic::LeavesOutAll(Manifest, Paths))
		{
			Reasons.Add(FString::Printf(
				TEXT("%s is not in this archive (left out of the selection)"), *Handler->DisplayName().ToString()));
		}
		else if (Handler->IsPresent(Manifest))
		{
			bCanProduce = true;
		}
		else
		{
			Handler->ExplainAbsence(Manifest, Reasons);
		}
	}
	if (!bRequested || bCanProduce)
	{
		return FString();
	}
	const FString StreamHint = FMantlePlaceIntegrityLogic::HoldsCesiumTerrain(Manifest)
		? FString(TEXT(" This bundle ships Cesium quantized-mesh terrain — use a Landscape import or "
					   "\"Stream into Cesium\" instead."))
		: FString();
	return FString::Printf(
		TEXT("Nothing to import: %s. (Existing assets left untouched.)%s"),
		*FString::Join(Reasons, TEXT("; ")), *StreamHint);
}

FString MantlePlaceImportOrchestration::WipePriorAssets(
	const FString& Identity, const FString& DestPackagePath, MantlePlaceImportTiming::FTimeline& Timeline)
{
	UEditorAssetSubsystem* AssetSubsystem = GEditor ? GEditor->GetEditorSubsystem<UEditorAssetSubsystem>() : nullptr;
	if (AssetSubsystem == nullptr)
	{
		return FString();
	}

	const bool bFolderExists = AssetSubsystem->DoesDirectoryExist(DestPackagePath);
	MantlePlaceImportProvenance::FRecord Prior;
	const bool bHasRecord = MantlePlaceImportProvenance::Read(Identity, Prior);
	const MantlePlaceImportProvenance::EVerdict Verdict =
		MantlePlaceImportProvenance::Classify(bFolderExists, bHasRecord, Prior, Identity);

	const FString Refusal = MantlePlaceImportProvenance::ExplainRefusal(Verdict, Prior, DestPackagePath);
	if (!Refusal.IsEmpty())
	{
		return Refusal;
	}

	if (Verdict == MantlePlaceImportProvenance::EVerdict::SameIdentity)
	{
		GEditor->SelectNone(false, true, false);
		const MantlePlaceImportTiming::FScopedPhase WipePhase(
			Timeline, MantlePlaceImportTiming::Phase::Wipe, TEXT("assets"));
		AssetSubsystem->DeleteDirectory(DestPackagePath);
		RetireDeletedLeftovers(DestPackagePath);
	}
	return FString();
}

namespace
{
TArray<UPackage*> PackagesUnder(const FString& Folder)
{
	const FString Prefix = Folder / TEXT("");
	TArray<UPackage*> Packages;
	ForEachObjectOfClass(UPackage::StaticClass(), [&Prefix, &Packages](UObject* Object)
	{
		if (Object->GetName().StartsWith(Prefix))
		{
			Packages.Add(CastChecked<UPackage>(Object));
		}
	});
	return Packages;
}
}

int32 MantlePlaceImportOrchestration::RetireDeletedLeftovers(const FString& DestPackagePath)
{
	TArray<UObject*> Leftovers;
	for (UPackage* Package : PackagesUnder(DestPackagePath))
	{
		ForEachObjectWithPackage(Package, [&Leftovers](UObject* Object)
		{
			if (!Object->HasAnyFlags(RF_Public))
			{
				Leftovers.Add(Object);
			}
			return true;
		}, false);
	}

	UPackage* Transient = GetTransientPackage();
	for (UObject* Leftover : Leftovers)
	{
		const FName Retired = MakeUniqueObjectName(Transient, Leftover->GetClass(),
			FName(*FString::Printf(TEXT("%s_Deleted"), *Leftover->GetName())));
		UE_LOG(LogMantlePlaceImport, Log,
			TEXT("Re-import: %s was deleted but is still held in memory; moved aside so the import "
				 "creates a new asset at its path."),
			*Leftover->GetPathName());
		Leftover->Rename(*Retired.ToString(), Transient, REN_DontCreateRedirectors | REN_NonTransactional | REN_DoNotDirty);
		Leftover->SetFlags(RF_Transient);
	}
	return Leftovers.Num();
}

namespace
{
int32 KeepOrDestroyEmptied(UWorld* World, const TArray<AActor*>& PartitionActors, TArray<TWeakObjectPtr<AActor>>* OutKept)
{
	if (OutKept == nullptr)
	{
		return MantlePlaceFoliageScatter::DestroyEmptyPartitionActors(World, PartitionActors);
	}
	int32 Kept = 0;
	for (AActor* PartitionActor : PartitionActors)
	{
		if (MantlePlaceFoliageScatter::HoldsNothing(PartitionActor))
		{
			OutKept->AddUnique(PartitionActor);
			++Kept;
		}
	}
	return Kept;
}
}

int32 MantlePlaceImportOrchestration::ReplacePriorActors(
	UWorld* World, const FString& Identity, MantlePlaceImportTiming::FTimeline& Timeline,
	TArray<TWeakObjectPtr<AActor>>* OutKeptPartitionActors)
{
	const MantlePlaceImportTiming::FScopedPhase WipeActorsPhase(
		Timeline, MantlePlaceImportTiming::Phase::Wipe, TEXT("actors"));
	const FName ImportTagName(*MantlePlaceImportNaming::ImportTag(Identity));
	TArray<AActor*> StaleActors;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (It->Tags.Contains(ImportTagName))
		{
			StaleActors.AddUnique(*It);
		}
	}

	const TArray<AActor*> PartitionActors = MantlePlaceFoliageScatter::CleanupGenerated(World, StaleActors);

	if (!UWorld::IsPartitionedWorld(World))
	{
		for (AActor* Stale : StaleActors)
		{
			World->EditorDestroyActor(Stale, true);
		}
		return StaleActors.Num() + KeepOrDestroyEmptied(World, PartitionActors, OutKeptPartitionActors);
	}

	const int32 TaggedCount = StaleActors.Num();
	for (int32 Index = 0; Index < TaggedCount; ++Index)
	{
		const ALandscape* Landscape = Cast<ALandscape>(StaleActors[Index]);
		const ULandscapeInfo* Info = Landscape != nullptr ? Landscape->GetLandscapeInfo() : nullptr;
		if (Info == nullptr)
		{
			continue;
		}
		for (const TWeakObjectPtr<ALandscapeStreamingProxy>& Proxy : Info->GetSortedStreamingProxies())
		{
			if (Proxy.IsValid())
			{
				StaleActors.AddUnique(Proxy.Get());
			}
		}
	}

	using MantlePlaceWorldPartitionLogic::EWipeRank;
	TArray<EWipeRank> Ranks;
	Ranks.Reserve(StaleActors.Num());
	for (const AActor* Stale : StaleActors)
	{
		Ranks.Add(Stale->IsA<ALandscapeStreamingProxy>() ? EWipeRank::StreamingProxy
			: Stale->IsA<ALandscape>()                   ? EWipeRank::LandscapeParent
			                                             : EWipeRank::Other);
	}
	for (const int32 Index : MantlePlaceWorldPartitionLogic::WipeOrder(Ranks))
	{
		World->EditorDestroyActor(StaleActors[Index], true);
	}
	return StaleActors.Num() + KeepOrDestroyEmptied(World, PartitionActors, OutKeptPartitionActors);
}

int32 MantlePlaceImportOrchestration::ReleaseKeptPartitionActors(
	UWorld* World, const TArray<TWeakObjectPtr<AActor>>& KeptPartitionActors)
{
	TArray<AActor*> Live;
	for (const TWeakObjectPtr<AActor>& PartitionActor : KeptPartitionActors)
	{
		if (PartitionActor.IsValid())
		{
			Live.Add(PartitionActor.Get());
		}
	}
	return MantlePlaceFoliageScatter::DestroyEmptyPartitionActors(World, Live);
}

namespace
{
TArray<FGuid> CollectUnloadedPriorActors(UWorldPartition* WorldPartition, UWorld* World, const FString& Identity)
{
	const FName ImportTagName(*MantlePlaceImportNaming::ImportTag(Identity));
	TSet<FGuid> PriorLandscapes;
	for (TActorIterator<ALandscape> It(World); It; ++It)
	{
		if (It->Tags.Contains(ImportTagName))
		{
			PriorLandscapes.Add(It->GetLandscapeGuid());
		}
	}

	TArray<FBox2D> PriorScatters;
	for (TActorIterator<AMantlePlaceFoliageVolume> It(World); It; ++It)
	{
		if (It->Tags.Contains(ImportTagName) && It->Bounds != nullptr)
		{
			const FBox Box = It->Bounds->Bounds.GetBox();
			PriorScatters.Add(FBox2D(FVector2D(Box.Min), FVector2D(Box.Max)));
		}
	}

	TArray<FGuid> Unloaded;
	FWorldPartitionHelpers::ForEachActorDescInstance(WorldPartition, AActor::StaticClass(),
		[&Unloaded, &ImportTagName, &PriorLandscapes, &PriorScatters](const FWorldPartitionActorDescInstance* Desc)
		{
			if (Desc == nullptr || Desc->IsLoaded())
			{
				return true;
			}
			if (MantlePlaceWorldPartitionLogic::IsDestroyedThisSession(ReadDescriptorPackage(*Desc)))
			{
				return true;
			}
			bool bPrior = Desc->GetTags().Contains(ImportTagName);
			if (!bPrior && PriorScatters.Num() > 0)
			{
				const UClass* NativeClass = Desc->GetActorNativeClass();
				if (NativeClass != nullptr && NativeClass->IsChildOf(APCGPartitionActor::StaticClass()))
				{
					const FBox Cell = Desc->GetEditorBounds();
					bPrior = Cell.IsValid != 0
						&& MantlePlaceWorldPartitionLogic::CellHoldsPriorScatter(
							FBox2D(FVector2D(Cell.Min), FVector2D(Cell.Max)), PriorScatters);
				}
			}
			if (!bPrior && PriorLandscapes.Num() > 0)
			{
				const UClass* NativeClass = Desc->GetActorNativeClass();
				if (NativeClass != nullptr && NativeClass->IsChildOf(ALandscapeProxy::StaticClass()))
				{
					const FPartitionActorDesc* PartitionDesc =
						static_cast<const FPartitionActorDesc*>(Desc->GetActorDesc());
					bPrior = PartitionDesc != nullptr && PriorLandscapes.Contains(PartitionDesc->GridGuid);
				}
			}
			if (bPrior)
			{
				Unloaded.Add(Desc->GetGuid());
			}
			return true;
		});
	return Unloaded;
}
}

FString MantlePlaceImportOrchestration::ExplainUnloadedPriorActors(UWorld* World, const FString& Identity)
{
	UWorldPartition* WorldPartition = World != nullptr ? World->GetWorldPartition() : nullptr;
	if (WorldPartition == nullptr)
	{
		return FString();
	}
	return MantlePlaceWorldPartitionLogic::ExplainUnloadedPriorActors(
		CollectUnloadedPriorActors(WorldPartition, World, Identity).Num());
}

FString MantlePlaceImportOrchestration::LoadUnloadedPriorActors(UWorld* World, const FString& Identity, FPriorActorLoad& OutLoad)
{
	OutLoad = FPriorActorLoad();
	UWorldPartition* WorldPartition = World != nullptr ? World->GetWorldPartition() : nullptr;
	if (WorldPartition == nullptr)
	{
		return FString();
	}
	const TArray<FGuid> Unloaded = CollectUnloadedPriorActors(WorldPartition, World, Identity);
	OutLoad.Unloaded = Unloaded.Num();
	if (Unloaded.IsEmpty())
	{
		return FString();
	}

	FWorldPartitionHelpers::FForEachActorWithLoadingParams Params;
	Params.ActorGuids = Unloaded;
	Params.bKeepReferences = true;
	FWorldPartitionHelpers::FForEachActorWithLoadingResult Loading;
	int32 Loaded = 0;
	FWorldPartitionHelpers::ForEachActorWithLoading(WorldPartition,
		[&Loaded](const FWorldPartitionActorDescInstance* Desc)
		{
			Loaded += (Desc != nullptr && Desc->IsLoaded()) ? 1 : 0;
			return true;
		},
		Params, Loading);
	OutLoad.Loaded = Loaded;
	OutLoad.References = MoveTemp(Loading.ActorReferences);

	const FString Refusal = MantlePlaceWorldPartitionLogic::ExplainUnloadablePriorActors(OutLoad.Unloaded, OutLoad.Loaded);
	if (!Refusal.IsEmpty())
	{
		UE_LOG(LogMantlePlaceImport, Warning,
			TEXT("Re-import: %d of %d actor(s) of the previous import in unloaded World Partition regions could not be loaded; refused."),
			OutLoad.Unloaded - OutLoad.Loaded, OutLoad.Unloaded);
		OutLoad.Release();
	}
	return Refusal;
}

namespace
{
void BeginHandler(FMantlePlaceImportContext& Context, const IMantlePlaceArtifactHandler& Handler)
{
	Context.Family = Handler.LayerFamily();
	Context.SourcePaths.Reset();
	Handler.CollectSourcePaths(Context.Manifest, Context.SourcePaths);
	Context.EquivalenceGroup = Handler.EquivalenceGroup();
}

bool LeftOutOfArchive(const FMantlePlaceVaultManifest& Manifest, const IMantlePlaceArtifactHandler& Handler)
{
	TArray<FString> Paths;
	Handler.CollectSourcePaths(Manifest, Paths);
	return FMantlePlaceIntegrityLogic::LeavesOutAll(Manifest, Paths);
}

bool Runs(const FMantlePlaceImportContext& Context, const IMantlePlaceArtifactHandler& Handler)
{
	if (!Context.Selection.Includes(Handler.Id()) || Context.Cut.Contains(Handler.Id()))
	{
		return false;
	}
	return !LeftOutOfArchive(Context.Manifest, Handler);
}

bool BeginStep(FMantlePlaceImportContext& Context, const IMantlePlaceArtifactHandler& Handler)
{
	if (Context.Steps == nullptr || Context.Steps->Begin(Handler.Id(), FPlatformTime::Seconds()))
	{
		return true;
	}
	Context.Cut.Add(Handler.Id());
	return false;
}
}

void MantlePlaceImportOrchestration::PlanSteps(
	const FHandlers& Handlers, const FMantlePlaceVaultManifest& Manifest, const FMantlePlaceImportSelection& Selection,
	FMantlePlaceImportSteps& Steps)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (!Selection.Includes(Handler->Id()))
		{
			continue;
		}
		if (!Handler->IsPresent(Manifest))
		{
			if (const FString* Reason = Manifest.FormatSkips.Find(Handler->Id()))
			{
				Steps.Add(Handler->Id(), Handler->DisplayName().ToString());
				Steps.Skip(Handler->Id(), *Reason);
			}
			continue;
		}
		Steps.Add(Handler->Id(), Handler->DisplayName().ToString());
		if (LeftOutOfArchive(Manifest, *Handler))
		{
			Steps.Skip(Handler->Id(), TEXT("Not in this download: it was left out of the selection."));
		}
	}
}

TArray<FString> MantlePlaceImportOrchestration::DescribeFormatSkips(
	const FHandlers& Handlers, const FMantlePlaceVaultManifest& Manifest, const FMantlePlaceImportSelection& Selection)
{
	TArray<FString> Lines;
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		const FString* Reason = Manifest.FormatSkips.Find(Handler->Id());
		if (Reason != nullptr && Selection.Includes(Handler->Id()) && !Handler->IsPresent(Manifest))
		{
			Lines.Add(FString::Printf(TEXT("Skipped %s: %s"), *Handler->DisplayName().ToString(), **Reason));
		}
	}
	Lines.Append(Manifest.FormatNotes);
	return Lines;
}

void MantlePlaceImportOrchestration::RunPrepare(const FHandlers& Handlers, FMantlePlaceImportContext& Context)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (!Runs(Context, *Handler))
		{
			continue;
		}
		const bool bStep = Context.Steps != nullptr && Handler->Prepares();
		if (bStep && !BeginStep(Context, *Handler))
		{
			continue;
		}
		BeginHandler(Context, *Handler);
		Handler->Prepare(Context);
		if (bStep)
		{
			Context.Steps->Pause(Handler->Id(), FPlatformTime::Seconds());
		}
	}
}

void MantlePlaceImportOrchestration::RunImport(const FHandlers& Handlers, FMantlePlaceImportContext& Context)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (Runs(Context, *Handler))
		{
			if (!BeginStep(Context, *Handler))
			{
				continue;
			}
			BeginHandler(Context, *Handler);
			const bool bSucceededBefore = Context.bAllRequestedSucceeded;
			Context.bAllRequestedSucceeded = true;
			Handler->Import(Context);
			const bool bHandlerSucceeded = Context.bAllRequestedSucceeded;
			Context.bAllRequestedSucceeded = bSucceededBefore && bHandlerSucceeded;
			if (Context.Steps != nullptr)
			{
				Context.Steps->End(Handler->Id(), bHandlerSucceeded, FPlatformTime::Seconds());
			}
		}
		else if (Context.Selection.Includes(Handler->Id()) && !Context.Cut.Contains(Handler->Id())
			&& Handler->IsPresent(Context.Manifest))
		{
			Context.Log.Add(FString::Printf(
				TEXT("%s skipped: what it is made from is not in this archive (left out of the selection)."),
				*Handler->DisplayName().ToString()));
		}
	}
}

void MantlePlaceImportOrchestration::RunFinalize(const FHandlers& Handlers, FMantlePlaceImportContext& Context)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (Runs(Context, *Handler))
		{
			BeginHandler(Context, *Handler);
			Handler->Finalize(Context);
		}
	}
}

TArray<FMantlePlaceLevelImportTag> MantlePlaceImportOrchestration::ReadLevelImports(UWorld* World)
{
	TArray<FMantlePlaceLevelImportTag> Found;
	if (World == nullptr)
	{
		return Found;
	}
	const FString ImportPrefix = MantlePlaceImportNaming::ImportTagPrefix();
	const FString FramePrefix = MantlePlaceImportNaming::FrameTagPrefix();
	const auto Read = [&](const TArray<FName>& Tags)
	{
		FMantlePlaceLevelImportTag Import;
		bool bImport = false;
		for (const FName& Tag : Tags)
		{
			const FString Text = Tag.ToString();
			if (Text.StartsWith(ImportPrefix))
			{
				Import.Identity = Text.RightChop(ImportPrefix.Len());
				bImport = true;
			}
			else if (Text.StartsWith(FramePrefix))
			{
				Import.FrameValue = Text.RightChop(FramePrefix.Len());
			}
		}
		if (bImport && !Import.Identity.IsEmpty())
		{
			Found.Add(Import);
		}
	};

	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Read(It->Tags);
	}
	if (UWorldPartition* WorldPartition = World->GetWorldPartition())
	{
		FWorldPartitionHelpers::ForEachActorDescInstance(WorldPartition, AActor::StaticClass(),
			[&Read](const FWorldPartitionActorDescInstance* Desc)
			{
				if (Desc != nullptr && !Desc->IsLoaded()
					&& !MantlePlaceWorldPartitionLogic::IsDestroyedThisSession(ReadDescriptorPackage(*Desc)))
				{
					Read(Desc->GetTags());
				}
				return true;
			});
	}
	return Found;
}
