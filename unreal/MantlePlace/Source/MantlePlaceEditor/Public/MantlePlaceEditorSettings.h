// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Engine/DeveloperSettings.h"

#include "MantlePlaceEditorSettings.generated.h"

/**
 * The plugin's project settings, under Project Settings -> Plugins -> Mantle Place.
 *
 * `config = Game` and `defaultconfig`, so a value is written to the project's DefaultGame.ini and
 * checked in — a decision the whole team shares. Deliberately NOT per-user: two developers with
 * different content roots in one project would each import the same order to a different place, and
 * neither one's re-import would ever clean up the other's copy. That is the exact failure the
 * order-keyed identity exists to prevent, so putting the root in per-user config would reintroduce
 * it through the back door.
 *
 * There is one knob, on purpose. A studio's asset-layout policy is the thing that genuinely
 * collides with a fixed `/Game/MantlePlace`, and it is about package paths. Per-asset name
 * templates are not offered: they would make every bug report begin with "what is your template?"
 * and make the naming gate unable to say what conforming looks like.
 */
UCLASS(config = Game, defaultconfig, meta = (DisplayName = "Mantle Place"))
class MANTLEPLACEEDITOR_API UMantlePlaceEditorSettings : public UDeveloperSettings
{
	GENERATED_BODY()

public:
	UMantlePlaceEditorSettings();

	/**
	 * Where imported bundle content is written. One folder per order is created beneath this.
	 *
	 * Must be a mount point plus at least one segment (`/Game/MantlePlace`, `/Game/Studio/Geo`).
	 * An unusable value falls back to the default rather than failing the import — the importer
	 * creates and force-deletes a directory beneath this, and a malformed root is not a thing to
	 * resolve creatively.
	 */
	UPROPERTY(EditAnywhere, config, Category = "Import",
		meta = (DisplayName = "Generated content root"))
	FString GeneratedContentRoot;

	/** The configured root if it is usable, the default otherwise. What the importer calls. */
	static FString ResolveGeneratedContentRoot();

	//~ Begin UDeveloperSettings interface
	virtual FName GetCategoryName() const override;
	//~ End UDeveloperSettings interface
};
