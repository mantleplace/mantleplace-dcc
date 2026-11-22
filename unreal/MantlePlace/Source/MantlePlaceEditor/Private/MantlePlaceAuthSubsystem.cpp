// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceAuthSubsystem.h"

#include "MantlePlaceAuthSystemBase.h"

#include "Editor.h"

void UMantlePlaceAuthSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
	Super::Initialize(Collection);

	AuthSystem = NewObject<UMantlePlaceAuthSystemBase>(this);
}

void UMantlePlaceAuthSubsystem::Deinitialize()
{
	if (AuthSystem != nullptr)
	{
		AuthSystem->CancelSignIn();
		AuthSystem = nullptr;
	}

	Super::Deinitialize();
}

UMantlePlaceAuthSubsystem* UMantlePlaceAuthSubsystem::Get()
{
	return GEditor != nullptr ? GEditor->GetEditorSubsystem<UMantlePlaceAuthSubsystem>() : nullptr;
}
