// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "MantlePlaceRoadSpline.generated.h"

class USplineComponent;

/**
 * One road centerline of a bundle's `road_splines` layer: a spline through the road's points in the
 * Local Projected Frame, and the attributes the platform published for it as real properties.
 *
 * It replaces a bare actor that carried the same three facts as tag strings. A property is what the
 * editor, a Blueprint and a PCG graph can read by name without parsing anything: select it in the
 * outliner, filter the level's actors by class, read `RoadClass` off one. Nothing in the plugin reads
 * a road attribute out of a tag, and the importer writes none.
 *
 * Geometry only. A road mesh or decal made from this spline is a later feature; this actor is the
 * thing it would be made from. It lives in the Runtime module so a saved level that holds one loads
 * in a cooked build, as the tree-points row type does.
 *
 * The attribute values are the platform's, applied verbatim (`HPS-33`): `EstimatedWidthM` is the
 * ETL's estimate and nothing here corrects it, and `RoadClass` is the class string as published, not
 * a vocabulary this plugin owns. `unreal/docs/road-spline-actor-contract.md` states each property.
 */
UCLASS(BlueprintType)
class MANTLEPLACERUNTIME_API AMantlePlaceRoadSpline : public AActor
{
	GENERATED_BODY()

public:
	AMantlePlaceRoadSpline();

	/** The centerline. The actor's root, in world space: the points are placed, not transformed. */
	UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = "Mantle Place|Road")
	TObjectPtr<USplineComponent> Spline;

	/** The road's class as the platform published it (`class` in the layer), or None where the feature carries none. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Road")
	FName RoadClass;

	/** The road's name as published (`name`), or empty where the feature carries none. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Road")
	FString RoadName;

	/** The ETL's width estimate in meters (`width_m_estimated`); 0 where the feature publishes none. */
	UPROPERTY(EditAnywhere, BlueprintReadOnly, Category = "Mantle Place|Road")
	float EstimatedWidthM = 0.0f;
};
