// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceHandover.h"

#include "MantlePlaceImportNaming.h"
#include "MantlePlaceImportOrchestration.h"
#include "MantlePlaceOdblLogic.h"

#include "Components/TextRenderComponent.h"
#include "Editor.h"
#include "Engine/TextRenderActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "GameFramework/Actor.h"
#include "HAL/IConsoleManager.h"
#include "ScopedTransaction.h"

#define LOCTEXT_NAMESPACE "MantlePlaceHandover"

namespace
{
/** The value of the first tag carrying this prefix; false when there is none. */
bool TagValue(const AActor& Actor, const FString& Prefix, FString& OutValue)
{
	for (const FName& Tag : Actor.Tags)
	{
		const FString Text = Tag.ToString();
		if (Text.StartsWith(Prefix))
		{
			OutValue = Text.Mid(Prefix.Len());
			return true;
		}
	}
	return false;
}

/** One actor of an import, read back from the tags Claim wrote. False for an actor that is not an import's. */
bool ReadItem(const AActor& Actor, MantlePlaceOdblLogic::FHandoverItem& OutItem)
{
	if (!TagValue(Actor, MantlePlaceImportNaming::ImportTagPrefix(), OutItem.Identity))
	{
		return false;
	}
	OutItem.bOdbl = Actor.Tags.Contains(MantlePlaceImportNaming::OdblTag());
	TagValue(Actor, MantlePlaceImportNaming::EquivalenceTagPrefix(), OutItem.EquivalenceGroup);
	const FString CreditPrefix = MantlePlaceImportNaming::CreditLineTagPrefix();
	for (const FName& Tag : Actor.Tags)
	{
		const FString Text = Tag.ToString();
		if (Text.StartsWith(CreditPrefix))
		{
			OutItem.CreditLines.AddUnique(Text.Mid(CreditPrefix.Len()));
		}
	}
	return true;
}

/** The credit actor this order already has, or null. */
ATextRenderActor* FindCreditActor(UWorld* World, const FString& Identity)
{
	const FName Tag(*MantlePlaceImportNaming::CreditActorTag(Identity));
	for (TActorIterator<ATextRenderActor> It(World); It; ++It)
	{
		if (It->Tags.Contains(Tag))
		{
			return *It;
		}
	}
	return nullptr;
}

/** Write (or refresh) the order's credit actor: a text actor in the order's outliner folder. */
ATextRenderActor* WriteCreditActor(UWorld* World, const MantlePlaceOdblLogic::FHandoverCredit& Credit)
{
	ATextRenderActor* Actor = FindCreditActor(World, Credit.Identity);
	if (Actor == nullptr)
	{
		Actor = World->SpawnActor<ATextRenderActor>(FVector::ZeroVector, FRotator::ZeroRotator);
	}
	if (Actor == nullptr)
	{
		return nullptr;
	}
	Actor->Modify();
	Actor->SetActorLabel(MantlePlaceImportNaming::CreditActorLabel(Credit.Identity));
	Actor->Tags.AddUnique(FName(*MantlePlaceImportNaming::CreditActorTag(Credit.Identity)));
	Actor->SetFolderPath(FName(*MantlePlaceImportNaming::OutlinerFolder(Credit.Identity)));
	if (UTextRenderComponent* Text = Actor->GetTextRender())
	{
		Text->SetText(FText::FromString(MantlePlaceOdblLogic::CreditText(Credit.Lines)));
		Text->SetWorldSize(40.0f);
	}
	return Actor;
}
}

FString FMantlePlaceHandoverReport::Summary() const
{
	TArray<FString> Lines;
	Lines.Add(FString::Printf(TEXT("Prepare for handover: %d ODbL actor(s) removed (%d swapped for a Produced Work), %d credit actor(s) written."),
		Removed.Num(), Swapped.Num(), CreditActors.Num()));
	Lines.Append(Notes);
	return FString::Join(Lines, TEXT("\n"));
}

FMantlePlaceHandoverReport MantlePlaceHandover::Prepare(UWorld* World)
{
	FMantlePlaceHandoverReport Report;
	if (World == nullptr)
	{
		Report.Notes.Add(TEXT("There is no level open to prepare."));
		return Report;
	}

	TArray<AActor*> Actors;
	TArray<FString> Labels; // read now: destroying a landscape takes its streaming proxies with it
	TArray<MantlePlaceOdblLogic::FHandoverItem> Items;
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		MantlePlaceOdblLogic::FHandoverItem Item;
		if (ReadItem(**It, Item))
		{
			Actors.Add(*It);
			Labels.Add(It->GetActorLabel());
			Items.Add(MoveTemp(Item));
		}
	}

	const MantlePlaceOdblLogic::FHandoverPlan Plan = MantlePlaceOdblLogic::PlanHandover(Items);

	const FScopedTransaction Transaction(LOCTEXT("PrepareForHandover", "Mantle Place: Prepare for handover"));

	// A Produced Work that stands in for removed ODbL content must be showing.
	for (int32 Index = 0; Index < Items.Num(); ++Index)
	{
		if (Plan.Decisions[Index] != MantlePlaceOdblLogic::EHandoverDecision::Swap)
		{
			continue;
		}
		for (int32 Other = 0; Other < Items.Num(); ++Other)
		{
			if (!Items[Other].bOdbl && Items[Other].Identity == Items[Index].Identity
				&& Items[Other].EquivalenceGroup == Items[Index].EquivalenceGroup)
			{
				if (IsValid(Actors[Other]))
				{
					Actors[Other]->Modify();
					Actors[Other]->SetIsTemporarilyHiddenInEditor(false);
				}
			}
		}
	}

	for (int32 Index = 0; Index < Items.Num(); ++Index)
	{
		if (Plan.Decisions[Index] == MantlePlaceOdblLogic::EHandoverDecision::Keep)
		{
			continue;
		}
		const FString& Label = Labels[Index];
		if (Plan.Decisions[Index] == MantlePlaceOdblLogic::EHandoverDecision::Swap)
		{
			Report.Swapped.Add(Label);
		}
		Report.Removed.Add(Label);
		if (IsValid(Actors[Index]))
		{
			World->EditorDestroyActor(Actors[Index], /*bShouldModifyLevel*/ true);
		}
	}

	for (const MantlePlaceOdblLogic::FHandoverCredit& Credit : Plan.Credits)
	{
		if (const ATextRenderActor* Written = WriteCreditActor(World, Credit))
		{
			Report.CreditActors.Add(Written->GetActorLabel());
		}
		else
		{
			Report.Notes.Add(FString::Printf(TEXT("Could not add the credit for %s."), *Credit.Identity));
		}
	}

	if (Items.IsEmpty())
	{
		Report.Notes.Add(TEXT("This level holds no Mantle Place import."));
	}
	else if (Report.Removed.IsEmpty())
	{
		Report.Notes.Add(TEXT("No ODbL content in this level; nothing was removed."));
	}
	if (Plan.Credits.IsEmpty() && !Report.Removed.IsEmpty())
	{
		Report.Notes.Add(TEXT("The bundle's manifest stated no credit for this content, so none could be added."));
	}
	if (UWorld::IsPartitionedWorld(World))
	{
		Report.Notes.Add(TEXT("Only loaded actors are handled: load the whole World Partition level before running this."));
	}
	return Report;
}

FMantlePlaceHandoverReport MantlePlaceHandover::PrepareEditorWorld()
{
	UWorld* World = GEditor != nullptr ? GEditor->GetEditorWorldContext().World() : nullptr;
	const FMantlePlaceHandoverReport Report = Prepare(World);
	UE_LOG(LogMantlePlaceImport, Display, TEXT("%s"), *Report.Summary());
	return Report;
}

static FAutoConsoleCommand GMantlePlacePrepareForHandover(
	TEXT("MantlePlace.PrepareForHandover"),
	TEXT("Remove (or swap for a Produced Work) the ODbL content the import filed under ODbL and add the credit, so the level is safe to ship or share."),
	FConsoleCommandDelegate::CreateStatic([]() { MantlePlaceHandover::PrepareEditorWorld(); }));

#undef LOCTEXT_NAMESPACE
