// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportProvenance.h"
#include "MantlePlaceImportStepsLogic.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceImporterLibrary.h"

#include "Components/PrimitiveComponent.h"
#include "Editor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "HAL/PlatformFileManager.h"
#include "HAL/PlatformMisc.h"
#include "Materials/MaterialInterface.h"
#include "Misc/Paths.h"
#include "UObject/Package.h"

// MantlePlace.Import.LiveCancel: Cancel between steps against a real bundle, and the import after it.
//
// MantlePlace.Import.Steps.Cancel and MantlePlace.Import.Orchestration.StepsAndCancel prove which steps
// a cancel stops; this proves what that leaves in a level. The bundle is MP_LIVETEST_BUNDLE (else the
// in-tree live-test copy), with LiveFreeTier's skip rule: no bundle warns and passes locally, and is an
// error when CI is set.
//
// The import is cancelled the moment its second artifact import begins, as a click on Cancel delivered
// at that boundary would. That step finishes and the later ones do not start. Then: the result says it was
// cancelled; the content that finished is recorded as this order's, so a re-import replaces it rather
// than refusing it as unrecorded; and every actor it left references only live assets, none of them a
// prior import's deleted leftover. Last, the same bundle is imported again with nobody cancelling, and
// that import completes.

namespace MantlePlaceImportLiveCancelTest
{
/**
 * The actors of this order whose components reference a material that is gone, by label: one garbage
 * collection has claimed, or a prior import's deleted asset that RetireDeletedLeftovers moved aside (into
 * the transient package, named `<name>_Deleted`). A transient material is not dead by itself: a landscape
 * proxy's components hold the editor's own transient instances.
 */
bool IsDeadMaterial(const UMaterialInterface* Material)
{
	return !IsValid(Material)
		|| (Material->GetOutermost() == GetTransientPackage() && Material->GetName().Contains(TEXT("_Deleted")));
}

TArray<FString> ActorsWithDeadReferences(UWorld* World, const FName& ImportTag)
{
	TArray<FString> Dead;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		if (!It->Tags.Contains(ImportTag))
		{
			continue;
		}
		TArray<UPrimitiveComponent*> Components;
		It->GetComponents(Components);
		for (UPrimitiveComponent* Component : Components)
		{
			TArray<UMaterialInterface*> Materials;
			Component->GetUsedMaterials(Materials);
			for (UMaterialInterface* Material : Materials)
			{
				if (Material != nullptr && IsDeadMaterial(Material))
				{
					Dead.AddUnique(It->GetActorLabel());
				}
			}
		}
	}
	return Dead;
}

int32 CountTagged(UWorld* World, const FName& ImportTag)
{
	int32 Count = 0;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		Count += It->Tags.Contains(ImportTag) ? 1 : 0;
	}
	return Count;
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportLiveCancelTest,
	"MantlePlace.Import.LiveCancel",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportLiveCancelTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceImportLiveCancelTest;

	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
	FString Zip = FPlatformMisc::GetEnvironmentVariable(TEXT("MP_LIVETEST_BUNDLE"));
	if (Zip.IsEmpty() || !Platform.FileExists(*Zip))
	{
		Zip = FPaths::ProjectSavedDir() / TEXT("MantlePlace/livetest/download.zip");
	}
	if (!Platform.FileExists(*Zip))
	{
		const FString Reason =
			FString::Printf(TEXT("Sample bundle not present (%s); skipping live cancel test."), *Zip);
		if (!FPlatformMisc::GetEnvironmentVariable(TEXT("CI")).IsEmpty())
		{
			AddError(Reason);
			return false;
		}
		AddWarning(Reason);
		return true;
	}

	UWorld* World = GEditor != nullptr ? GEditor->GetEditorWorldContext().World() : nullptr;
	if (World == nullptr)
	{
		AddError(TEXT("No editor world available."));
		return false;
	}

	// --- Cancelled when the second artifact import begins: it finishes, the rest do not start. -------------
	// The second, so the level holds finished content from more than one step; an import, not a Prepare
	// half, so the step it lands on runs to Done. A step reads Done only once its Import has run, so
	// "another artifact step is Done" is what says the Import pass is under way.
	FMantlePlaceImportSteps Steps;
	FName CancelledAt;
	Steps.OnChanged = [&Steps, &CancelledAt]()
	{
		const int32 Current = Steps.CurrentIndex();
		bool bAnArtifactDone = false;
		for (int32 Index = 1; Index < Steps.Steps().Num(); ++Index) // index 0 is the integrity check
		{
			bAnArtifactDone |= Steps.Steps()[Index].State == EMantlePlaceImportStepState::Done;
		}
		if (Current > 0 && bAnArtifactDone && CancelledAt.IsNone())
		{
			CancelledAt = Steps.Steps()[Current].Id;
			Steps.RequestCancel();
		}
	};
	const FMantlePlaceImportResult Cancelled =
		UMantlePlaceImporterLibrary::ImportVaultPackageWithSteps(Zip, EMantlePlaceImportMode::Both, &Steps);
	AddInfo(Cancelled.Message);
	if (!TestTrue(TEXT("the bundle had a step after the integrity check to cancel at"), !CancelledAt.IsNone()))
	{
		return false;
	}
	AddInfo(FString::Printf(TEXT("Cancelled when step '%s' began."), *CancelledAt.ToString()));

	TestTrue(TEXT("the result says it was cancelled"), Cancelled.bCancelled);
	TestFalse(TEXT("a cancelled import is not a success"), Cancelled.bSuccess);
	TestTrue(TEXT("the steps are finished"), Steps.IsFinished());
	TestFalse(TEXT("no row is left reading Importing"), Steps.CurrentIndex() != INDEX_NONE);
	bool bAfterCut = false;
	for (const FMantlePlaceImportStep& Step : Steps.Steps())
	{
		AddInfo(FString::Printf(TEXT("  %s: %s %s"), *Step.Name, *FMantlePlaceImportSteps::StateWord(Step.State), *Step.Reason));
		if (Step.Id == CancelledAt)
		{
			TestTrue(TEXT("the step running when Cancel came ran to its end"),
				Step.State == EMantlePlaceImportStepState::Done || Step.State == EMantlePlaceImportStepState::Failed);
			bAfterCut = true;
		}
		else if (bAfterCut && Step.Reason.IsEmpty())
		{
			TestTrue(*FString::Printf(TEXT("'%s', after the cancel, did not run"), *Step.Name),
				Step.State == EMantlePlaceImportStepState::NotRun || Step.State == EMantlePlaceImportStepState::Cancelled);
		}
	}

	MantlePlaceImportProvenance::FRecord Record;
	TestTrue(TEXT("what the cancelled import left is recorded as this order's, so a re-import replaces it"),
		MantlePlaceImportProvenance::Read(Cancelled.Identity, Record));
	const FName ImportTag(*MantlePlaceImportNaming::ImportTag(Cancelled.Identity));
	AddInfo(FString::Printf(TEXT("%d actor(s) of this order in the level after the cancel."), CountTagged(World, ImportTag)));
	const TArray<FString> Dead = ActorsWithDeadReferences(World, ImportTag);
	TestTrue(*FString::Printf(TEXT("every actor the cancelled import left references live assets (dead: %s)"),
		*FString::Join(Dead, TEXT(", "))), Dead.Num() == 0);

	// --- Importing again, uncancelled, completes. ----------------------------------------------------------
	const FMantlePlaceImportResult Again =
		UMantlePlaceImporterLibrary::ImportVaultPackage(Zip, EMantlePlaceImportMode::Both);
	if (!TestTrue(TEXT("importing again after the cancel completes"), Again.bSuccess))
	{
		AddInfo(Again.Message);
		return false;
	}
	TestFalse(TEXT("the re-import was not cancelled"), Again.bCancelled);
	TestEqual(TEXT("the same order"), Again.Identity, Cancelled.Identity);
	const TArray<FString> DeadAfter = ActorsWithDeadReferences(World, ImportTag);
	TestTrue(*FString::Printf(TEXT("every actor the re-import left references live assets (dead: %s)"),
		*FString::Join(DeadAfter, TEXT(", "))), DeadAfter.Num() == 0);
	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
