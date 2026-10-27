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
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Subsystems/EditorAssetSubsystem.h"
#include "UObject/Package.h"
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

	// ODbL content: where an actor made from `database` files lands, decided from the files' stated roles.
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
		// Through CreditTags, never one FName per line: a line past FName's length is an assertion.
		for (const FString& CreditTag : MantlePlaceOdblLogic::CreditTags(
				 MantlePlaceOdblLogic::CreditLines(Manifest.Attribution, Manifest.FileLicensing, SourcePaths)))
		{
			ExtraTags.Add(FName(*CreditTag));
		}
	}
	const auto Mark = [&](AActor* Marked)
	{
		Marked->Tags.AddUnique(Tag);
		for (const FName& Extra : ExtraTags)
		{
			Marked->Tags.AddUnique(Extra);
		}
		Marked->SetFolderPath(Folder);
	};

	Actor->SetActorLabel(Label);
	Mark(Actor);

	// World Partition only; a non-partitioned level's actors are claimed exactly as before.
	if (!UWorld::IsPartitionedWorld(Actor->GetWorld()))
	{
		return;
	}

	// One Data Layer per layer family, shared across orders: the actor joins its handler's family.
	MantlePlaceDataLayers::Assign(Actor, ContentRoot, Family);
	if (bOdbl)
	{
		MantlePlaceDataLayers::AssignOdbl(Actor, ContentRoot);
	}

	// One HLOD layer per HLOD family (Terrain, Buildings), shared across orders like the Data Layer.
	MantlePlaceHLODs::Assign(Actor, ContentRoot, Family);

	// The unloaded-actor policy: kept always loaded, so every loaded-actor walk of the import sees it
	// (ShouldForceNonSpatiallyLoaded says which walks, and why that outlived v1). A landscape and the
	// HLOD families stream instead: an always-loaded actor is never in an HLOD, and a re-import loads
	// the unloaded ones before its wipe (LoadUnloadedPriorActors).
	const bool bStreamedFamily = Actor->IsA<ALandscapeProxy>() || MantlePlaceHLODLogic::HasHLOD(Family);
	if (MantlePlaceWorldPartitionLogic::ShouldForceNonSpatiallyLoaded(/*bPartitioned*/ true, bStreamedFamily)
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

/**
 * What memory holds of an unloaded descriptor's package, read without loading anything. A package
 * not in memory reads as all false: its actor was never loaded this session, so it is unloaded.
 */
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
	State.bHoldsLiveActor = AActor::FindActorInPackage(Package, /*bEvenIfPendingKill*/ false) != nullptr;
	return State;
}
}

// ── The orchestration steps ────────────────────────────────────────────────

bool MantlePlaceImportOrchestration::VerifyAll(
	const FMantlePlaceVaultManifest& Manifest, const FZipArchiveReader& Reader,
	MantlePlaceImportTiming::FTimeline& Timeline, FString& OutError, TArray<FString>* OutNotes)
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
	int32 LeftOutCount = 0;
	for (const FMantlePlaceDeclaredArtifact& Artifact : Declared)
	{
		// A payload the archive's own `view` says it was cut without is not read, hashed or refused:
		// the selection left it out. It is named, so the log never silently lacks it. A payload in a
		// part the archive claims to hold still falls through, and a missing entry there is refused.
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
			// The bundle ships it, but this archive was cut without it (RunImport skips it the same way).
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
		RetireDeletedLeftovers(DestPackagePath);
	}
	return FString();
}

namespace
{
/** Every package in memory under Folder, the folder's own path excluded. */
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
	// dcc #349. The force-delete clears RF_Public and RF_Standalone on each asset and leaves freeing
	// it to garbage collection; anything holding a counted reference keeps it in memory, at its own
	// path, in its own package. The live editor did exactly that to a saved SM_Buildings
	// (LogReferenceChain: "not currently reachable but it does have 1 refcount set"). The glTF
	// import that follows finds the object there and imports INTO it, flags still cleared, so the
	// new buildings actor references a private object: Save All refuses its package ("Illegal
	// reference to private object") and the mesh's own package saves no asset, and after a reopen
	// the buildings are gone. Moved to the transient package instead, the path is free and the
	// import creates a fresh public asset; whatever held the old one still holds a valid object.
	//
	// Only what the delete deleted: a top-level object it left public it did not delete, and the
	// import replaces that in place as it always has. Outside the import's transaction, and not
	// transacted itself: it deletes nothing, so it leaves the undo buffer alone.
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
		}, /*bIncludeNestedObjects*/ false);
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
// The prior scatter's emptied partition actors: kept for the import that follows when the caller
// takes them (ReplacePriorActors says why), destroyed now when it does not. Returns how many.
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

	// A foliage scatter's generated instances live on PCG partition actors, which carry no tag and
	// are shared per cell: swept before its volume goes, and each destroyed once nothing else uses it,
	// or kept for the re-import to generate on (KeepOrDestroyEmptied).
	// The sweep reaches loaded ones only; LoadUnloadedPriorActors has loaded the unloaded ones first.
	const TArray<AActor*> PartitionActors = MantlePlaceFoliageScatter::CleanupGenerated(World, StaleActors);

	// A non-partitioned level is wiped exactly as it always was: no proxies, iteration order.
	if (!UWorld::IsPartitionedWorld(World))
	{
		for (AActor* Stale : StaleActors)
		{
			World->EditorDestroyActor(Stale, /*bShouldModifyLevel*/ true);
		}
		return StaleActors.Num() + KeepOrDestroyEmptied(World, PartitionActors, OutKeptPartitionActors);
	}

	// A split landscape's proxies go with it, tagged or not: a proxy the landscape tools added
	// after the import never got the tag. Resolved from the parent's landscape info, which holds
	// every loaded proxy; LoadUnloadedPriorActors has loaded the unloaded ones first.
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
/**
 * The descriptor walk ExplainUnloadedPriorActors and LoadUnloadedPriorActors share: the GUIDs of the
 * prior import's actors that are in unloaded regions. Reads the level's actor descriptors, which
 * exist for loaded and unloaded actors alike and record each one's tags; loads nothing.
 */
TArray<FGuid> CollectUnloadedPriorActors(UWorldPartition* WorldPartition, UWorld* World, const FString& Identity)
{
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

	// The prior import's foliage scatters, by footprint: their instances live on PCG partition
	// actors, untagged and spatially loaded per cell. The volume itself is kept always loaded; were
	// it unloaded, its own tag counts it below.
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
			// A wipe earlier this session destroyed it: the descriptor reads as unloaded until the
			// save that deletes its package, and the actor is gone, not in an unloaded region.
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
		// Not partitioned: every actor is loaded, and there is nothing to refuse.
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
		// Not partitioned: every actor is loaded, and there is nothing to load.
		return FString();
	}
	const TArray<FGuid> Unloaded = CollectUnloadedPriorActors(WorldPartition, World, Identity);
	OutLoad.Unloaded = Unloaded.Num();
	if (Unloaded.IsEmpty())
	{
		return FString();
	}

	// The engine's own load-through-the-descriptors walk, given exactly the prior actors: it loads
	// each one with the actors it references (a reference to an unloaded one would not resolve) and,
	// asked to keep them, collects no garbage and hands the references over.
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
		// Never partial: what did load is unloaded again, and the re-import changes nothing.
		UE_LOG(LogMantlePlaceImport, Warning,
			TEXT("Re-import: %d of %d actor(s) of the previous import in unloaded World Partition regions could not be loaded; refused."),
			OutLoad.Unloaded - OutLoad.Loaded, OutLoad.Unloaded);
		OutLoad.Release();
	}
	return Refusal;
}

namespace
{
/** What Claim needs to know about the handler about to run: its family, its source files, its equivalence group. */
void BeginHandler(FMantlePlaceImportContext& Context, const IMantlePlaceArtifactHandler& Handler)
{
	Context.Family = Handler.LayerFamily();
	Context.SourcePaths.Reset();
	Handler.CollectSourcePaths(Context.Manifest, Context.SourcePaths);
	Context.EquivalenceGroup = Handler.EquivalenceGroup();
}

/**
 * Whether the import runs this handler: the selection includes it, and the archive does not lack
 * everything it would make from. An artifact whose payloads the archive was cut without has nothing
 * to place; it is skipped (and named once, in RunImport), never failed.
 */
bool Runs(const FMantlePlaceImportContext& Context, const IMantlePlaceArtifactHandler& Handler)
{
	if (!Context.Selection.Includes(Handler.Id()))
	{
		return false;
	}
	TArray<FString> Paths;
	Handler.CollectSourcePaths(Context.Manifest, Paths);
	return !FMantlePlaceIntegrityLogic::LeavesOutAll(Context.Manifest, Paths);
}
}

void MantlePlaceImportOrchestration::RunPrepare(const FHandlers& Handlers, FMantlePlaceImportContext& Context)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (Runs(Context, *Handler))
		{
			BeginHandler(Context, *Handler);
			Handler->Prepare(Context);
		}
	}
}

void MantlePlaceImportOrchestration::RunImport(const FHandlers& Handlers, FMantlePlaceImportContext& Context)
{
	for (const TUniquePtr<IMantlePlaceArtifactHandler>& Handler : Handlers)
	{
		if (Runs(Context, *Handler))
		{
			BeginHandler(Context, *Handler);
			Handler->Import(Context);
		}
		else if (Context.Selection.Includes(Handler->Id()) && Handler->IsPresent(Context.Manifest))
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
