// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceImportOrchestration.h"

#include "MantlePlaceDataLayers.h"
#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceImportTiming.h"
#include "MantlePlaceIntegrityLogic.h"
#include "MantlePlaceSha256.h"
#include "MantlePlaceWorldPartitionLogic.h"

#include "Editor.h"
#include "EngineUtils.h"
#include "FileUtilities/ZipArchiveReader.h"
#include "GameFramework/Actor.h"
#include "Landscape.h"
#include "LandscapeInfo.h"
#include "LandscapeStreamingProxy.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Subsystems/EditorAssetSubsystem.h"
#include "WorldPartition/ActorPartition/PartitionActorDesc.h"
#include "WorldPartition/WorldPartition.h"
#include "WorldPartition/WorldPartitionActorDescInstance.h"
#include "WorldPartition/WorldPartitionHelpers.h"

DEFINE_LOG_CATEGORY(LogMantlePlaceImport);

// ── The context's helpers ──────────────────────────────────────────────────

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
	const FName Folder(*MantlePlaceImportNaming::OutlinerFolder(Identity));
	Actor->SetActorLabel(Label);
	Actor->Tags.AddUnique(Tag);
	Actor->SetFolderPath(Folder);

	// World Partition only; a non-partitioned level's actors are claimed exactly as before.
	if (!UWorld::IsPartitionedWorld(Actor->GetWorld()))
	{
		return;
	}

	// One Data Layer per layer family, shared across orders: the actor joins its handler's family.
	MantlePlaceDataLayers::Assign(Actor, ContentRoot, Family);

	// v1 unloaded-actor policy: kept always loaded, so the tag-matched wipe always sees it.
	const bool bLandscapeFamily = Actor->IsA<ALandscapeProxy>();
	if (MantlePlaceWorldPartitionLogic::ShouldForceNonSpatiallyLoaded(/*bPartitioned*/ true, bLandscapeFamily)
		&& Actor->CanChangeIsSpatiallyLoadedFlag())
	{
		Actor->SetIsSpatiallyLoaded(false);
	}

	// A split landscape's streaming proxies are this import's too. They keep their engine-generated
	// names (the label is cosmetic; the tag is what the wipe matches) and stay spatially loaded.
	if (const ALandscape* Landscape = Cast<ALandscape>(Actor))
	{
		if (const ULandscapeInfo* Info = Landscape->GetLandscapeInfo())
		{
			for (const TWeakObjectPtr<ALandscapeStreamingProxy>& Proxy : Info->GetSortedStreamingProxies())
			{
				if (Proxy.IsValid())
				{
					Proxy->Tags.AddUnique(Tag);
					Proxy->SetFolderPath(Folder);
					MantlePlaceDataLayers::Assign(Proxy.Get(), ContentRoot, Family);
				}
			}
		}
	}
}

namespace
{
/**
 * The integrity pre-check's cost, split into the two halves that have different remedies: getting
 * a payload's bytes out of the zip, and hashing them. Both are totals over the declared chain, so
 * neither has a scope to put an ordinary guard on.
 *
 * One type rather than two accumulators passed side by side, because they are never apart: they
 * are created together, handed down together, and — the part that made this worth a type —
 * RECORDED together, in the order they ran, on every path out of the loop. That last one was two
 * pairs of lines at two call sites before it was a method, which is one copy away from a failed
 * import printing its rows in a different order from a successful one.
 *
 * ⛔ Read `Read` as read-and-inflate. `TryReadFile` is a disk read AND a zlib inflate, so it is
 * not "the I/O half" and a large number there could be either.
 */
struct FIntegrityTiming
{
	explicit FIntegrityTiming(MantlePlaceImportTiming::FTimeline& Timeline)
		: Read(Timeline, MantlePlaceImportTiming::Phase::IntegrityEntryRead)
		, Digest(Timeline, MantlePlaceImportTiming::Phase::IntegrityDigest)
	{
	}

	/**
	 * Both rows, in the order they ran.
	 *
	 * Called rather than left to the destructors, which fire in reverse declaration order and
	 * would print the hashing row above the reading row that fed it. Idempotent underneath, so
	 * the destructors add nothing after this.
	 */
	void Record()
	{
		Read.Record();
		Digest.Record();
	}

	MantlePlaceImportTiming::FAccumulatedPhase Read;
	MantlePlaceImportTiming::FAccumulatedPhase Digest;
};

/**
 * Hash one zip entry's bytes and compare to the manifest's declared sha256. Returns true (skip) when
 * ExpectedHex is empty (a payload with no published digest); on a read failure or a mismatch it fills
 * OutError and returns false so the caller can abort the import.
 *
 * `Timing` is taken as a parameter rather than reached for through the running import because the
 * caller's loop is what makes these totals: this function measures one payload's two spans and
 * adds them, and owning no row of its own is what stops the row count becoming a property of the
 * bundle.
 */
bool VerifyEntrySha256(
	const FZipArchiveReader& Reader,
	const FString& InZipPath,
	const FString& ExpectedHex,
	FIntegrityTiming& Timing,
	FString& OutError)
{
	if (ExpectedHex.IsEmpty())
	{
		return true; // nothing declared to verify against
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
		Actual = MantlePlaceSha256::HexDigest(Bytes);
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
}

// ── The orchestration steps ────────────────────────────────────────────────

bool MantlePlaceImportOrchestration::VerifyAll(
	const FMantlePlaceVaultManifest& Manifest, const FZipArchiveReader& Reader,
	MantlePlaceImportTiming::FTimeline& Timeline, FString& OutError)
{
	const MantlePlaceImportTiming::FScopedPhase IntegrityPhase(
		Timeline, MantlePlaceImportTiming::Phase::IntegrityPrecheck);

	// The pre-check's two halves, summed across every payload rather than recorded per payload:
	// the row count would otherwise depend on the bundle, and nothing could ask "how long was
	// spent hashing" without knowing to add them up. Declared here, inside the phase that
	// encloses them, so the rows land nested under it rather than beside it.
	FIntegrityTiming IntegrityTiming(Timeline);

	const TArray<FMantlePlaceDeclaredArtifact> Declared =
		FMantlePlaceIntegrityLogic::CollectDeclaredArtifacts(Manifest);
	int32 VerifiedCount = 0;
	int32 UnverifiableCount = 0;
	for (const FMantlePlaceDeclaredArtifact& Artifact : Declared)
	{
		FString IntegrityError;
		if (!VerifyEntrySha256(Reader, Artifact.Path, Artifact.Sha256, IntegrityTiming, IntegrityError))
		{
			// Recorded on this path too, so a failed pre-check's two rows come out in the same
			// order as a successful one's. A timeline you cannot line up against another
			// timeline is the one thing the timing exists to prevent.
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

	// The verification gate is a product claim ("verified before anything is written"), so its
	// PASSING is narrated, not only its failure — log followers should see the gate clear before
	// the first actor spawns. It counts both states, because a bundle whose payloads are half
	// unverifiable is a fact about that bundle, not about this check.
	UE_LOG(LogMantlePlaceImport, Log,
		TEXT("Integrity verified: %d of %d declared payload(s) matched their manifest sha256, "
			 "%d carried no digest and are valid but unverified (jobId %s)."),
		VerifiedCount, Declared.Num(), UnverifiableCount,
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
		if (Handler->IsPresent(Manifest))
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
	return FString::Printf(
		TEXT("Nothing to import: %s. (Existing assets left untouched.) This bundle ships Cesium "
			 "quantized-mesh terrain — use a Landscape import or \"Stream into Cesium\" instead."),
		*FString::Join(Reasons, TEXT("; ")));
}

FString MantlePlaceImportOrchestration::WipePriorAssets(
	const FString& Identity, const FString& DestPackagePath, MantlePlaceImportTiming::FTimeline& Timeline)
{
	UEditorAssetSubsystem* AssetSubsystem = GEditor ? GEditor->GetEditorSubsystem<UEditorAssetSubsystem>() : nullptr;
	if (AssetSubsystem == nullptr)
	{
		return FString();
	}

	// Guarded, because the folder is named by the TRUNCATED identity and the delete is a force-
	// delete. Two identities sharing eight characters name the same folder, and without the
	// provenance record this would silently destroy a different order's imported content. The
	// record is read from outside the content tree on purpose — reading an asset inside a folder
	// we are about to force-delete is the hazard described at the delete itself.
	const bool bFolderExists = AssetSubsystem->DoesDirectoryExist(DestPackagePath);
	MantlePlaceImportProvenance::FRecord Prior;
	const bool bHasRecord = MantlePlaceImportProvenance::Read(Identity, Prior);
	const MantlePlaceImportProvenance::EVerdict Verdict =
		MantlePlaceImportProvenance::Classify(bFolderExists, bHasRecord, Prior, Identity);

	const FString Refusal = MantlePlaceImportProvenance::ExplainRefusal(Verdict, Prior, DestPackagePath);
	if (!Refusal.IsEmpty())
	{
		// Before the transaction opens and before anything is created, so a refusal changes
		// nothing at all.
		return Refusal;
	}

	if (Verdict == MantlePlaceImportProvenance::EVerdict::SameIdentity)
	{
		// Clear the editor selection first: DeleteDirectory force-deletes, and force-deleting a
		// selected/referenced asset drives UpdatePivotLocationForSelection over a now-stale typed-
		// element selection, tripping the "Element type ID 0 not registered" ensure. An empty
		// selection makes that path a no-op.
		GEditor->SelectNone(/*bNoteSelectionChange*/ false, /*bDeselectBSPSurfs*/ true, /*bWarnAboutManyActors*/ false);
		const MantlePlaceImportTiming::FScopedPhase WipePhase(
			Timeline, MantlePlaceImportTiming::Phase::Wipe, TEXT("assets"));
		AssetSubsystem->DeleteDirectory(DestPackagePath);
	}
	return FString();
}

int32 MantlePlaceImportOrchestration::ReplacePriorActors(
	UWorld* World, const FString& Identity, MantlePlaceImportTiming::FTimeline& Timeline)
{
	// Its absence is also the legacy signal: an actor from 0.3.0 or earlier has an MP_* label and no
	// tag, so it is not matched here — and the content guard (WipePriorAssets) has already refused
	// the import rather than leaving those actors orphaned beside new ones.
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

	// A non-partitioned level is wiped exactly as it always was: no proxies, iteration order.
	if (!UWorld::IsPartitionedWorld(World))
	{
		for (AActor* Stale : StaleActors)
		{
			World->EditorDestroyActor(Stale, /*bShouldModifyLevel*/ true);
		}
		return StaleActors.Num();
	}

	// A split landscape's proxies go with it, tagged or not: a proxy the landscape tools added
	// after the import never got the tag. Resolved from the parent's landscape info, which holds
	// every loaded proxy; an unloaded one is ExplainUnloadedPriorActors' to refuse, before this.
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

	// Proxies before their parent: destroying the parent first leaves its proxies registered to a
	// landscape info with no owner.
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
		World->EditorDestroyActor(StaleActors[Index], /*bShouldModifyLevel*/ true);
	}
	return StaleActors.Num();
}

FString MantlePlaceImportOrchestration::ExplainUnloadedPriorActors(UWorld* World, const FString& Identity)
{
	UWorldPartition* WorldPartition = World != nullptr ? World->GetWorldPartition() : nullptr;
	if (WorldPartition == nullptr)
	{
		// Not partitioned: every actor is loaded, and there is nothing to refuse.
		return FString();
	}

	// The prior import's landscapes, by GUID: their proxies may be unloaded AND untagged.
	const FName ImportTagName(*MantlePlaceImportNaming::ImportTag(Identity));
	TSet<FGuid> PriorLandscapes;
	for (TActorIterator<ALandscape> It(World); It; ++It)
	{
		if (It->Tags.Contains(ImportTagName))
		{
			PriorLandscapes.Add(It->GetLandscapeGuid());
		}
	}

	// A read of the level's actor descriptors, which exist for loaded and unloaded actors alike and
	// record each one's tags. Nothing is loaded and nothing is destroyed here; loading and
	// destroying through this walk is the v2 upgrade, not this.
	int32 Unloaded = 0;
	FWorldPartitionHelpers::ForEachActorDescInstance(WorldPartition, AActor::StaticClass(),
		[&Unloaded, &ImportTagName, &PriorLandscapes](const FWorldPartitionActorDescInstance* Desc)
		{
			if (Desc == nullptr || Desc->IsLoaded())
			{
				return true;
			}
			bool bPrior = Desc->GetTags().Contains(ImportTagName);
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
			Unloaded += bPrior ? 1 : 0;
			return true;
		});
	return MantlePlaceWorldPartitionLogic::ExplainUnloadedPriorActors(Unloaded);
}

void MantlePlaceImportOrchestration::RunPrepare(const FHandlers& Handlers, FMantlePlaceImportContext& Context)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (Context.Selection.Includes(Handler->Id()))
		{
			Context.Family = Handler->LayerFamily();
			Handler->Prepare(Context);
		}
	}
}

void MantlePlaceImportOrchestration::RunImport(const FHandlers& Handlers, FMantlePlaceImportContext& Context)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (Context.Selection.Includes(Handler->Id()))
		{
			Context.Family = Handler->LayerFamily();
			Handler->Import(Context);
		}
	}
}

void MantlePlaceImportOrchestration::RunFinalize(const FHandlers& Handlers, FMantlePlaceImportContext& Context)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (Context.Selection.Includes(Handler->Id()))
		{
			Context.Family = Handler->LayerFamily();
			Handler->Finalize(Context);
		}
	}
}
