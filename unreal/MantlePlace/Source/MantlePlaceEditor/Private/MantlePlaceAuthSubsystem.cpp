// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceAuthSubsystem.h"

#include "MantlePlaceAuthSystemBase.h"

#include "Editor.h" // GEditor

void UMantlePlaceAuthSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
	Super::Initialize(Collection);

	// The plain C++ base, which reads its endpoint config from its own CDO
	// ([/Script/MantlePlaceRuntime.MantlePlaceAuthSystemBase] in the consuming project's
	// DefaultGame.ini). Constructing it here rather than on first use is the point of the
	// subsystem: one session for the editor, created at a defined moment.
	AuthSystem = NewObject<UMantlePlaceAuthSystemBase>(this);
}

void UMantlePlaceAuthSubsystem::Deinitialize()
{
	if (AuthSystem != nullptr)
	{
		// A sign-in still waiting on the browser holds a bound loopback route and a timeout ticker.
		// UMantlePlaceAuthSystemBase::BeginDestroy would eventually do this, but "eventually" is
		// whenever GC next runs, and the editor is shutting down.
		AuthSystem->CancelSignIn();
		AuthSystem = nullptr;
	}

	Super::Deinitialize();
}

UMantlePlaceAuthSubsystem* UMantlePlaceAuthSubsystem::Get()
{
	return GEditor != nullptr ? GEditor->GetEditorSubsystem<UMantlePlaceAuthSubsystem>() : nullptr;
}
