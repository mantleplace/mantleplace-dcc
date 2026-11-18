// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceImportTypes.h"
#include "MantlePlaceImporterLibrary.h"
#include "MantlePlaceEditorSettings.h"
#include "MantlePlaceScopedRestore.h"
#include "MantlePlaceRoofTraceLogic.h"
#include "MantlePlaceVaultTypes.h"

#include "AssetCompilingManager.h"
#include "Components/StaticMeshComponent.h"
#include "Editor.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "Engine/World.h"
#include "EngineUtils.h"
#include "FileHelpers.h"
#include "HAL/PlatformFileManager.h"
#include "HAL/PlatformMisc.h"
#include "Landscape.h"
#include "LandscapeDataAccess.h"
#include "LandscapeEdit.h"
#include "LandscapeHeightfieldCollisionComponent.h"
#include "LandscapeInfo.h"
#include "LandscapeStreamingProxy.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "PhysicsEngine/BodySetup.h"
#include "Rendering/NaniteResources.h"
#include "StaticMeshResources.h"

// Terrain collision after a real import, measured the way a game asks for it: a world query, not a
// property read. A whole bundle is imported in each mode (Landscape, Mesh, Both) into a fresh plain level
// and into a fresh partitioned World Partition level, and at a grid of points across the terrain:
//
//  - a line trace down (the Visibility channel, simple collision, as a default query is) must hit a
//    terrain actor at the rendered ground, within one heightmap sample's relief of it;
//  - a sphere swept down (the Pawn channel: where a dropped body comes to rest) must hit it too, so
//    nothing falls through;
//  - exactly one terrain representation may block: in Both mode the mesh overlay must not collide on
//    top of the Landscape;
//  - in the World Partition level every streaming proxy carries collision once loaded, and traces on
//    the proxies' shared borders hit;
//  - the building massing collides as its roofs: at every grid point over the buildings where a
//    complex (per-triangle) trace finds a roof, a default trace hits the buildings at that roof, not at
//    a hull or box above it. Where the drawn ground lies over the roof (a building levelled to its lowest
//    ground, on ground that rises across it by more than its height), the trace must hit that ground
//    instead, and the terrain must collide where it is drawn there: FMantlePlaceRoofTraceLogic judges each
//    point, and at most a small share of a level's roof points may lie under the drawn ground.
//
// The rendered ground is the Landscape's own height data (the heights its components draw), read back
// from the first Landscape the run imports, so the Mesh cases are held to the same ground. The terrain
// mesh is a Nanite mesh (Interchange builds Nanite by default in 5.8), so its collision is the Nanite
// case: whatever it carries, the table names it. In the World Partition level the Landscape is split one
// component per proxy, the smallest grid the plugin's setting takes, so even a small bundle has borders
// between proxies to trace across.
//
// The bundle is MP_LIVETEST_BUNDLE (the sealed archive of a bundle), as for MantlePlace.Import.LiveFreeTier:
// a missing bundle, or one with no heightmap, warns and passes locally and fails where CI is set. A bundle
// older than 3.0 with no Terrain.glb is measured in Landscape mode only, with a warning naming the modes left
// out; a 3.x bundle with no Terrain.glb fails, because the 3.0 file table holds the terrain mesh.
// Each case opens a new unsaved map, and the run reopens the map it started on.

namespace MantlePlaceTerrainCollisionLiveTest
{
constexpr int32 GridPerAxis = 24;
constexpr double SweepRadiusCm = 25.0;
constexpr double SlackCm = 2.0;
constexpr int32 RoofGridPerAxis = 64;

bool IsCi() { return !FPlatformMisc::GetEnvironmentVariable(TEXT("CI")).IsEmpty(); }

/** The rendered ground: the first imported Landscape's own height data, kept past its world. */
struct FGround
{
	bool bValid = false;
	FTransform ActorToWorld;
	int32 MinX = 0, MinY = 0, MaxX = 0, MaxY = 0;
	TArray<uint16> Heights;
	double MinZ = 0.0, MaxZ = 0.0;

	int32 Width() const { return MaxX - MinX + 1; }

	double LocalZ(int32 X, int32 Y) const
	{
		X = FMath::Clamp(X, MinX, MaxX);
		Y = FMath::Clamp(Y, MinY, MaxY);
		return LandscapeDataAccess::GetLocalHeight(Heights[(Y - MinY) * Width() + (X - MinX)]);
	}

	double WorldZ(double LocalHeight) const { return ActorToWorld.TransformPosition(FVector(0, 0, LocalHeight)).Z; }

	bool Capture(ALandscape* Landscape)
	{
		ULandscapeInfo* Info = Landscape != nullptr ? Landscape->GetLandscapeInfo() : nullptr;
		if (Info == nullptr || !Info->GetLandscapeExtent(MinX, MinY, MaxX, MaxY))
		{
			return false;
		}
		ActorToWorld = Landscape->LandscapeActorToWorld();
		Heights.SetNumZeroed(Width() * (MaxY - MinY + 1));
		FLandscapeEditDataInterface Edit(Info);
		Edit.GetHeightDataFast(MinX, MinY, MaxX, MaxY, Heights.GetData(), Width());
		MinZ = TNumericLimits<double>::Max();
		MaxZ = TNumericLimits<double>::Lowest();
		for (int32 Y = MinY; Y <= MaxY; ++Y)
		{
			for (int32 X = MinX; X <= MaxX; ++X)
			{
				const double Z = WorldZ(LocalZ(X, Y));
				MinZ = FMath::Min(MinZ, Z);
				MaxZ = FMath::Max(MaxZ, Z);
			}
		}
		bValid = true;
		return true;
	}

	/** The rendered height under a world point, and one heightmap sample's relief around it. */
	void At(const FVector2D& World, double& OutZ, double& OutRelief) const
	{
		const FVector Local = ActorToWorld.InverseTransformPosition(FVector(World, 0.0));
		const int32 X0 = FMath::FloorToInt32(Local.X);
		const int32 Y0 = FMath::FloorToInt32(Local.Y);
		const double Fx = Local.X - X0;
		const double Fy = Local.Y - Y0;
		const double H = FMath::Lerp(FMath::Lerp(LocalZ(X0, Y0), LocalZ(X0 + 1, Y0), Fx),
			FMath::Lerp(LocalZ(X0, Y0 + 1), LocalZ(X0 + 1, Y0 + 1), Fx), Fy);
		OutZ = WorldZ(H);
		double Lo = TNumericLimits<double>::Max();
		double Hi = TNumericLimits<double>::Lowest();
		for (int32 Dy = -1; Dy <= 2; ++Dy)
		{
			for (int32 Dx = -1; Dx <= 2; ++Dx)
			{
				const double Z = WorldZ(LocalZ(X0 + Dx, Y0 + Dy));
				Lo = FMath::Min(Lo, Z);
				Hi = FMath::Max(Hi, Z);
			}
		}
		OutRelief = Hi - Lo;
	}

	/** World XY at a fraction across the Landscape's extent. */
	FVector2D Point(double U, double V) const
	{
		const FVector Local(FMath::Lerp(double(MinX), double(MaxX), U), FMath::Lerp(double(MinY), double(MaxY), V), 0.0);
		const FVector World = ActorToWorld.TransformPosition(Local);
		return FVector2D(World.X, World.Y);
	}

	bool Inside(const FVector2D& World, double MarginSamples) const
	{
		const FVector Local = ActorToWorld.InverseTransformPosition(FVector(World, 0.0));
		return Local.X >= MinX + MarginSamples && Local.X <= MaxX - MarginSamples
			&& Local.Y >= MinY + MarginSamples && Local.Y <= MaxY - MarginSamples;
	}
};

struct FCase
{
	FString Level;
	FString Mode;
	bool bImported = false;
	int32 Points = 0;
	int32 Hits = 0;
	int32 Within = 0;
	int32 SweepHits = 0;
	double MaxAbsDzCm = 0.0;
	double SumDzCm = 0.0;
	bool bLandscapeBlocks = false;
	bool bMeshBlocks = false;
	int32 Proxies = 0;
	int32 ProxiesWithCollision = 0;
	int32 ProxyCentreHits = 0;
	int32 BorderPoints = 0;
	int32 BorderHits = 0;
	FString MeshCollision;
	int32 RoofPoints = 0;
	int32 RoofWithin = 0;
	double RoofMaxAbsDzCm = 0.0;
	int32 CoveredPoints = 0;
	int32 CoveredAtGround = 0;
	TArray<FString> CoveredNotes;
	TArray<FString> Misses;

	int32 Representations() const { return (bLandscapeBlocks ? 1 : 0) + (bMeshBlocks ? 1 : 0); }

	FString Row() const
	{
		if (!bImported)
		{
			return FString::Printf(TEXT("| %s | %s | not imported |"), *Level, *Mode);
		}
		return FString::Printf(
			TEXT("| %s | %s | traces hit %d/%d | at ground %d/%d | max |dz| %.2f m, mean dz %.2f m | sweeps hit %d/%d | "
				 "colliding terrain representations %d (landscape %s, mesh %s) | proxies with collision %d/%d, centre hits %d | "
				 "border hits %d/%d | open roofs hit at the roof %d/%d, max |dz| %.2f m | roofs under the drawn ground hit at the ground %d/%d | "
				 "buried roof points %d of %d (%.2f %%) | mesh: %s |"),
			*Level, *Mode, Hits, Points, Within, Points, MaxAbsDzCm / 100.0, Points > 0 ? SumDzCm / Points / 100.0 : 0.0,
			SweepHits, Points, Representations(), bLandscapeBlocks ? TEXT("yes") : TEXT("no"),
			bMeshBlocks ? TEXT("yes") : TEXT("no"), ProxiesWithCollision, Proxies, ProxyCentreHits, BorderHits, BorderPoints,
			RoofWithin, RoofPoints, RoofMaxAbsDzCm / 100.0, CoveredAtGround, CoveredPoints, CoveredPoints,
			CoveredPoints + RoofPoints, 100.0 * FMantlePlaceRoofTraceLogic::BuriedFraction(CoveredPoints, RoofPoints),
			MeshCollision.IsEmpty() ? TEXT("-") : *MeshCollision);
	}
};

const TCHAR* TraceFlagName(ECollisionTraceFlag Flag)
{
	switch (Flag)
	{
	case CTF_UseDefault: return TEXT("project default");
	case CTF_UseSimpleAndComplex: return TEXT("simple and complex");
	case CTF_UseSimpleAsComplex: return TEXT("simple as complex");
	case CTF_UseComplexAsSimple: return TEXT("complex as simple");
	default: return TEXT("?");
	}
}

FString DescribeMeshCollision(const AStaticMeshActor* Actor)
{
	const UStaticMeshComponent* Component = Actor != nullptr ? Actor->GetStaticMeshComponent() : nullptr;
	const UStaticMesh* Mesh = Component != nullptr ? Component->GetStaticMesh() : nullptr;
	if (Mesh == nullptr)
	{
		return TEXT("no mesh");
	}
	const UBodySetup* Body = Mesh->GetBodySetup();
	const int32 LodZeroTriangles = Mesh->GetRenderData() != nullptr && Mesh->GetRenderData()->LODResources.Num() > 0
		? Mesh->GetRenderData()->LODResources[0].GetNumTriangles()
		: 0;
	const FStaticMeshRenderData* RenderData = Mesh->GetRenderData();
	const int32 NaniteTriangles = RenderData != nullptr && RenderData->NaniteResourcesPtr.IsValid()
		? static_cast<int32>(RenderData->NaniteResourcesPtr->NumInputTriangles)
		: 0;
	return FString::Printf(
		TEXT("component %s (profile %s); body %s, simple shapes: %d box %d convex %d sphere %d capsule; Nanite %s; "
			 "collision triangles (render LOD0) %d, Nanite input triangles %d"),
		Component->GetCollisionEnabled() == ECollisionEnabled::NoCollision ? TEXT("no collision") : TEXT("collides"),
		*Component->GetCollisionProfileName().ToString(),
		Body != nullptr ? TraceFlagName(Body->CollisionTraceFlag) : TEXT("none"),
		Body != nullptr ? Body->AggGeom.BoxElems.Num() : 0, Body != nullptr ? Body->AggGeom.ConvexElems.Num() : 0,
		Body != nullptr ? Body->AggGeom.SphereElems.Num() : 0, Body != nullptr ? Body->AggGeom.SphylElems.Num() : 0,
		Mesh->IsNaniteEnabled() ? TEXT("on") : TEXT("off"), LodZeroTriangles,
		NaniteTriangles);
}

/**
 * The terrain actors of the level: every Landscape proxy, and the terrain mesh (the label the importer gives
 * it). The building massing (its label) is also listed in OutBuildings; for the terrain it is one of the rest.
 */
void FindTerrain(UWorld* World, TArray<AActor*>& OutLandscape, TArray<AActor*>& OutMesh, TArray<AActor*>& OutOther,
	TArray<AActor*>& OutBuildings, const FString& MeshLabelPrefix, const FString& BuildingsLabelPrefix)
{
	for (TActorIterator<AActor> It(World); It; ++It)
	{
		AActor* Actor = *It;
		if (Actor->IsA<ALandscapeProxy>())
		{
			OutLandscape.Add(Actor);
		}
		else if (Actor->IsA<AStaticMeshActor>() && Actor->GetActorLabel().StartsWith(MeshLabelPrefix))
		{
			OutMesh.Add(Actor);
		}
		else
		{
			OutOther.Add(Actor);
			if (Actor->IsA<AStaticMeshActor>() && Actor->GetActorLabel().StartsWith(BuildingsLabelPrefix))
			{
				OutBuildings.Add(Actor);
			}
		}
	}
}

bool TraceDown(UWorld* World, const FVector2D& XY, const FGround& Ground, const TArray<AActor*>& Ignore, FHitResult& Hit)
{
	FCollisionQueryParams Params(SCENE_QUERY_STAT(MantlePlaceTerrainCollisionTrace), /*bTraceComplex*/ false);
	Params.AddIgnoredActors(Ignore);
	return World->LineTraceSingleByChannel(Hit, FVector(XY, Ground.MaxZ + 10000.0), FVector(XY, Ground.MinZ - 10000.0),
		ECC_Visibility, Params);
}

bool SweepDown(UWorld* World, const FVector2D& XY, const FGround& Ground, const TArray<AActor*>& Ignore, FHitResult& Hit)
{
	FCollisionQueryParams Params(SCENE_QUERY_STAT(MantlePlaceTerrainCollisionSweep), /*bTraceComplex*/ false);
	Params.AddIgnoredActors(Ignore);
	return World->SweepSingleByChannel(Hit, FVector(XY, Ground.MaxZ + 10000.0), FVector(XY, Ground.MinZ - 10000.0),
		FQuat::Identity, ECC_Pawn, FCollisionShape::MakeSphere(SweepRadiusCm), Params);
}

/**
 * The building massing, traced onto its roofs. A roof point is where a complex trace (the mesh's own
 * triangles, whatever its simple collision is) going down onto the buildings alone meets an upward-facing
 * surface. A default trace through the whole level (simple collision) must then meet the first surface drawn
 * there: the buildings at the roof where the roof is open, or the ground where the drawn ground lies over the
 * roof (a building on ground that rises across it by more than its height). FMantlePlaceRoofTraceLogic judges
 * each point; every one is judged, and each that fails is described.
 */
void MeasureRoofs(UWorld* World, const FGround& Ground, const TArray<AActor*>& Landscape, const TArray<AActor*>& Mesh,
	const TArray<AActor*>& Other, const TArray<AActor*>& Buildings, FCase& Out)
{
	TArray<AActor*> Terrain = Landscape;
	Terrain.Append(Mesh);
	for (AActor* Actor : Buildings)
	{
		AStaticMeshActor* Massing = Cast<AStaticMeshActor>(Actor);
		UStaticMeshComponent* Component = Massing != nullptr ? Massing->GetStaticMeshComponent() : nullptr;
		if (Component == nullptr)
		{
			continue;
		}
		const FBox Box = Component->Bounds.GetBox();
		const double Top = FMath::Max(Box.Max.Z, Ground.MaxZ) + 10000.0;
		const double Bottom = FMath::Min(Box.Min.Z, Ground.MinZ) - 10000.0;
		const FCollisionQueryParams Complex(SCENE_QUERY_STAT(MantlePlaceRoofComplexTrace), /*bTraceComplex*/ true);
		const FCollisionQueryParams Simple(SCENE_QUERY_STAT(MantlePlaceRoofTrace), /*bTraceComplex*/ false);
		FCollisionQueryParams TerrainOnly(SCENE_QUERY_STAT(MantlePlaceRoofTerrainTrace), /*bTraceComplex*/ false);
		TerrainOnly.AddIgnoredActors(Other);
		for (int32 J = 0; J < RoofGridPerAxis; ++J)
		{
			for (int32 I = 0; I < RoofGridPerAxis; ++I)
			{
				const FVector2D XY(FMath::Lerp(Box.Min.X, Box.Max.X, (I + 0.5) / RoofGridPerAxis),
					FMath::Lerp(Box.Min.Y, Box.Max.Y, (J + 0.5) / RoofGridPerAxis));
				FHitResult Roof;
				if (!Component->LineTraceComponent(Roof, FVector(XY, Top), FVector(XY, Bottom), Complex)
					|| Roof.ImpactNormal.Z < 0.7)
				{
					continue;
				}
				FMantlePlaceRoofSample Sample;
				Sample.RoofZ = Roof.ImpactPoint.Z;
				FHitResult Under;
				Sample.bTerrain = World->LineTraceSingleByChannel(Under, FVector(XY, Top), FVector(XY, Bottom), ECC_Visibility, TerrainOnly)
					&& Terrain.Contains(Under.GetActor());
				Sample.TerrainZ = Under.ImpactPoint.Z;
				Ground.At(XY, Sample.GroundZ, Sample.GroundReliefCm);
				FHitResult Hit;
				Sample.bHit = World->LineTraceSingleByChannel(Hit, FVector(XY, Top), FVector(XY, Bottom), ECC_Visibility, Simple);
				Sample.bHitBuildings = Sample.bHit && Hit.GetActor() == Massing;
				Sample.bHitTerrain = Sample.bHit && Terrain.Contains(Hit.GetActor());
				Sample.HitZ = Hit.ImpactPoint.Z;

				const bool bCovered = FMantlePlaceRoofTraceLogic::IsCovered(Sample);
				const EMantlePlaceRoofTraceVerdict Verdict = FMantlePlaceRoofTraceLogic::Judge(Sample);
				const FString Where = FString::Printf(TEXT("(%.1f, %.1f): roof z %.1f, terrain %s z %.1f, rendered ground z %.1f (relief %.1f)"),
					XY.X, XY.Y, Sample.RoofZ, Sample.bTerrain ? TEXT("hit") : TEXT("not hit"), Sample.TerrainZ, Sample.GroundZ,
					Sample.GroundReliefCm);
				if (bCovered)
				{
					++Out.CoveredPoints;
					Out.CoveredAtGround += Verdict == EMantlePlaceRoofTraceVerdict::AtCoveringGround ? 1 : 0;
					Out.CoveredNotes.Add(Where);
				}
				else
				{
					++Out.RoofPoints;
					if (Verdict == EMantlePlaceRoofTraceVerdict::AtRoof)
					{
						++Out.RoofWithin;
						Out.RoofMaxAbsDzCm = FMath::Max(Out.RoofMaxAbsDzCm, FMath::Abs(Sample.HitZ - Sample.RoofZ));
					}
				}
				if (Verdict == EMantlePlaceRoofTraceVerdict::Missed)
				{
					Out.Misses.Add(FString::Printf(TEXT("%s; the default trace %s %s at z %.1f"), *Where,
						Sample.bHit ? TEXT("hit") : TEXT("hit nothing"),
						Sample.bHit && Hit.GetActor() != nullptr ? *Hit.GetActor()->GetActorLabel() : TEXT("-"), Sample.HitZ));
				}
			}
		}
	}
}

void FinishCompiling()
{
	FAssetCompilingManager::Get().FinishAllCompilation();
	FlushRenderingCommands();
}

/** Measure the level as it stands, against the ground. Mesh actors outside `Mesh` are ignored with the rest. */
void Measure(UWorld* World, const FGround& Ground, const TArray<AActor*>& Landscape, const TArray<AActor*>& Mesh,
	const TArray<AActor*>& Other, FCase& Out)
{
	TArray<AActor*> IgnoreOther = Other;
	TArray<AActor*> IgnoreOtherAndMesh = Other;
	IgnoreOtherAndMesh.Append(Mesh);
	TArray<AActor*> IgnoreOtherAndLandscape = Other;
	IgnoreOtherAndLandscape.Append(Landscape);

	for (int32 J = 0; J < GridPerAxis; ++J)
	{
		for (int32 I = 0; I < GridPerAxis; ++I)
		{
			const FVector2D XY = Ground.Point(0.02 + 0.96 * I / (GridPerAxis - 1), 0.02 + 0.96 * J / (GridPerAxis - 1));
			double Expected = 0.0;
			double Relief = 0.0;
			Ground.At(XY, Expected, Relief);
			++Out.Points;

			FHitResult Hit;
			if (TraceDown(World, XY, Ground, IgnoreOther, Hit))
			{
				++Out.Hits;
				const double Dz = Hit.ImpactPoint.Z - Expected;
				Out.SumDzCm += Dz;
				Out.MaxAbsDzCm = FMath::Max(Out.MaxAbsDzCm, FMath::Abs(Dz));
				Out.Within += FMath::Abs(Dz) <= Relief + SlackCm ? 1 : 0;
			}
			FHitResult Swept;
			Out.SweepHits += SweepDown(World, XY, Ground, IgnoreOther, Swept) ? 1 : 0;

			FHitResult Only;
			Out.bLandscapeBlocks |= Landscape.Num() > 0 && TraceDown(World, XY, Ground, IgnoreOtherAndMesh, Only);
			Out.bMeshBlocks |= Mesh.Num() > 0 && TraceDown(World, XY, Ground, IgnoreOtherAndLandscape, Only);
		}
	}

	// World Partition: each streaming proxy, loaded, carries collision, and its centre and its borders are hit.
	for (AActor* Actor : Landscape)
	{
		ALandscapeStreamingProxy* Proxy = Cast<ALandscapeStreamingProxy>(Actor);
		if (Proxy == nullptr)
		{
			continue;
		}
		++Out.Proxies;
		int32 Live = 0;
		for (ULandscapeHeightfieldCollisionComponent* Collision : Proxy->CollisionComponents)
		{
			Live += (Collision != nullptr && Collision->IsPhysicsStateCreated()
						&& Collision->GetCollisionEnabled() != ECollisionEnabled::NoCollision) ? 1 : 0;
		}
		Out.ProxiesWithCollision += (Live > 0 && Live == Proxy->CollisionComponents.Num()) ? 1 : 0;

		const FBox Box = Proxy->GetComponentsBoundingBox(/*bNonColliding*/ true);
		FHitResult Hit;
		const FVector2D Centre(Box.GetCenter().X, Box.GetCenter().Y);
		Out.ProxyCentreHits += (TraceDown(World, Centre, Ground, IgnoreOtherAndMesh, Hit) && Hit.GetActor() == Proxy) ? 1 : 0;

		for (int32 K = 0; K <= 8; ++K)
		{
			const double T = K / 8.0;
			const FVector2D Edges[] = {
				FVector2D(Box.Min.X, FMath::Lerp(Box.Min.Y, Box.Max.Y, T)),
				FVector2D(Box.Max.X, FMath::Lerp(Box.Min.Y, Box.Max.Y, T)),
				FVector2D(FMath::Lerp(Box.Min.X, Box.Max.X, T), Box.Min.Y),
				FVector2D(FMath::Lerp(Box.Min.X, Box.Max.X, T), Box.Max.Y),
			};
			for (const FVector2D& Edge : Edges)
			{
				// Only the borders shared with another proxy: the Landscape's own outer edge is not a seam.
				if (!Ground.Inside(Edge, 1.0))
				{
					continue;
				}
				++Out.BorderPoints;
				Out.BorderHits += TraceDown(World, Edge, Ground, IgnoreOtherAndMesh, Hit) ? 1 : 0;
			}
		}
	}
}

}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceTerrainCollisionLiveTest,
	"MantlePlace.Import.LiveTerrainCollision",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceTerrainCollisionLiveTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceTerrainCollisionLiveTest;

	const bool bStrict = IsCi();
	auto SkipOrFail = [this, bStrict](const FString& Reason) -> bool
	{
		if (bStrict)
		{
			AddError(FString::Printf(TEXT("%s CI is set, so this is a failure rather than a skip."), *Reason));
			return false;
		}
		AddWarning(Reason);
		return true;
	};
	if (GEditor == nullptr)
	{
		return SkipOrFail(TEXT("No editor: the terrain collision check needs an editor world. Skipped."));
	}

	IPlatformFile& Platform = FPlatformFileManager::Get().GetPlatformFile();
	FString Zip = FPlatformMisc::GetEnvironmentVariable(TEXT("MP_LIVETEST_BUNDLE"));
	if (Zip.IsEmpty() || !Platform.FileExists(*Zip))
	{
		Zip = FPaths::ProjectSavedDir() / TEXT("MantlePlace/livetest/download.zip");
	}
	if (!Platform.FileExists(*Zip))
	{
		return SkipOrFail(FString::Printf(TEXT("Sample bundle not present (%s); terrain collision not measured."), *Zip));
	}
	FMantlePlaceVaultManifest Manifest;
	FString ReadError;
	if (!UMantlePlaceImporterLibrary::ReadVaultManifest(Zip, Manifest, ReadError) || !Manifest.bValid)
	{
		return SkipOrFail(FString::Printf(TEXT("The bundle's manifest could not be read (%s)."), *ReadError));
	}
	// The ground every case is held to is read back from a Landscape, so a bundle with no heightmap has nothing
	// to measure. The terrain mesh is a member of the 3.0 file table (spec/format.md section 6,
	// `Elevation/Terrain.glb`), so a 3.x bundle without it fails. Before 3.0 the pointers decided whether a
	// bundle held one, so an older bundle with no Terrain.glb is measured in Landscape mode only: the Mesh and
	// Both modes need the mesh, and the warning names what was not measured.
	if (!Manifest.Heightmap.bPresent)
	{
		return SkipOrFail(TEXT("The bundle holds no heightmap, so there is no Landscape to measure the terrain against."));
	}
	const FMantlePlaceManifestVersion Version = FMantlePlaceManifestVersion::Parse(Manifest.Version);
	const bool bMeshPromised = Version.bValid && Version.Major >= 3;
	const bool bMeshModes = Manifest.Mesh.bPresent;
	if (!bMeshModes)
	{
		if (bMeshPromised)
		{
			AddError(FString::Printf(TEXT("The bundle is MPB %s, whose file table holds Elevation/Terrain.glb, and it holds no Terrain.glb: "
				"the Mesh and Both modes cannot be measured."), *Manifest.Version));
			return false;
		}
		AddWarning(FString::Printf(TEXT("The bundle (MPB %s, before 3.0) holds no Terrain.glb: the Mesh and Both modes were not measured, "
			"the Landscape mode was."), *Manifest.Version));
	}

	UWorld* StartWorld = GEditor->GetEditorWorldContext().World();
	const FString StartMap = StartWorld != nullptr ? StartWorld->GetOutermost()->GetName() : FString();

	const FString MeshLabelPrefix = TEXT("MP_Mesh_");
	const FString BuildingsLabelPrefix = TEXT("MP_Buildings_");
	FGround Ground;
	TArray<FCase> Cases;

	struct FMode
	{
		EMantlePlaceImportMode Mode;
		const TCHAR* Name;
	};
	const FMode Modes[] = {
		{ EMantlePlaceImportMode::Landscape, TEXT("Landscape") },
		{ EMantlePlaceImportMode::Mesh, TEXT("Mesh") },
		{ EMantlePlaceImportMode::Both, TEXT("Both") },
	};
	for (const bool bPartitioned : { false, true })
	{
		const FString Level = bPartitioned ? TEXT("World Partition") : TEXT("plain");
		UMantlePlaceEditorSettings* Settings = GetMutableDefault<UMantlePlaceEditorSettings>();
		const int32 GridSizeBefore = Settings->WorldPartitionGridSize;
		const FMantlePlaceScopedRestore RestoreGridSize([Settings, GridSizeBefore]() { Settings->WorldPartitionGridSize = GridSizeBefore; });
		Settings->WorldPartitionGridSize = 1;
		for (const FMode& Mode : Modes)
		{
			if (!bMeshModes && Mode.Mode != EMantlePlaceImportMode::Landscape)
			{
				continue;
			}
			FCase& Case = Cases.AddDefaulted_GetRef();
			Case.Level = Level;
			Case.Mode = Mode.Name;

			UWorld* World = GEditor->NewMap(bPartitioned);
			if (!TestNotNull(*FString::Printf(TEXT("%s %s: a new map opens"), *Level, Mode.Name), World))
			{
				continue;
			}
			TestEqual(*FString::Printf(TEXT("%s %s: the map is the level type under test"), *Level, Mode.Name),
				UWorld::IsPartitionedWorld(World), bPartitioned);

			const FMantlePlaceImportResult Result = UMantlePlaceImporterLibrary::ImportVaultPackage(Zip, Mode.Mode);
			FinishCompiling();
			AddInfo(FString::Printf(TEXT("%s %s import: success=%d\n%s"), *Level, Mode.Name, Result.bSuccess ? 1 : 0,
				*Result.Message));

			TArray<AActor*> Landscape, Mesh, Other, Buildings;
			FindTerrain(World, Landscape, Mesh, Other, Buildings, MeshLabelPrefix, BuildingsLabelPrefix);
			if (!Ground.bValid)
			{
				for (AActor* Actor : Landscape)
				{
					if (ALandscape* Parent = Cast<ALandscape>(Actor); Parent != nullptr && Ground.Capture(Parent))
					{
						break;
					}
				}
			}
			Case.bImported = (Landscape.Num() + Mesh.Num()) > 0;
			if (!TestTrue(*FString::Printf(TEXT("%s %s: the import placed terrain"), *Level, Mode.Name), Case.bImported)
				|| !TestTrue(TEXT("the rendered ground was read from a Landscape import"), Ground.bValid))
			{
				continue;
			}
			if (Mesh.Num() > 0)
			{
				Case.MeshCollision = DescribeMeshCollision(Cast<AStaticMeshActor>(Mesh[0]));
			}
			Measure(World, Ground, Landscape, Mesh, Other, Case);
			MeasureRoofs(World, Ground, Landscape, Mesh, Other, Buildings, Case);

			const FString Name = FString::Printf(TEXT("%s level, %s mode"), *Level, Mode.Name);
			TestEqual(*FString::Printf(TEXT("%s: every trace down hits the terrain (no fall-through)"), *Name), Case.Hits, Case.Points);
			TestEqual(*FString::Printf(TEXT("%s: every dropped body (sphere sweep) lands on the terrain"), *Name), Case.SweepHits, Case.Points);
			TestEqual(*FString::Printf(TEXT("%s: every trace hits the rendered ground, within one heightmap sample"), *Name),
				Case.Within, Case.Points);
			TestEqual(*FString::Printf(TEXT("%s: exactly one terrain representation collides"), *Name), Case.Representations(), 1);
			if (Mode.Mode != EMantlePlaceImportMode::Mesh)
			{
				TestTrue(*FString::Printf(TEXT("%s: the Landscape is the one that collides"), *Name), Case.bLandscapeBlocks);
			}
			if (Manifest.Buildings.bPresent)
			{
				TestTrue(*FString::Printf(TEXT("%s: at least one open roof point was judged"), *Name), Case.RoofPoints > 0);
				TestTrue(*FString::Printf(TEXT("%s: %d of %d roof points (%.2f %%) lie under the drawn ground, at most %.0f %% may"), *Name,
					Case.CoveredPoints, Case.CoveredPoints + Case.RoofPoints,
					100.0 * FMantlePlaceRoofTraceLogic::BuriedFraction(Case.CoveredPoints, Case.RoofPoints),
					100.0 * FMantlePlaceRoofTraceLogic::MaxBuriedFraction),
					FMantlePlaceRoofTraceLogic::IsBuriedWithinBound(Case.CoveredPoints, Case.RoofPoints));
				TestEqual(*FString::Printf(TEXT("%s: every trace onto an open roof hits the buildings at the roof, not a hull above it"), *Name),
					Case.RoofWithin, Case.RoofPoints);
				TestEqual(*FString::Printf(TEXT("%s: every trace onto a roof the drawn ground lies over hits that ground"), *Name),
					Case.CoveredAtGround, Case.CoveredPoints);
				if (Case.CoveredPoints > 0)
				{
					AddInfo(FString::Printf(TEXT("%s: %d roof point(s) lie under the drawn ground (a building on ground rising across it "
						"by more than its height):\n%s"), *Name, Case.CoveredPoints, *FString::Join(Case.CoveredNotes, TEXT("\n"))));
				}
				if (Case.Misses.Num() > 0)
				{
					AddInfo(FString::Printf(TEXT("%s: roof points the default trace did not meet as drawn:\n%s"), *Name,
						*FString::Join(Case.Misses, TEXT("\n"))));
				}
			}
			if (bPartitioned && Mode.Mode != EMantlePlaceImportMode::Mesh)
			{
				TestTrue(*FString::Printf(TEXT("%s: the Landscape is split into streaming proxies"), *Name), Case.Proxies > 1);
				TestEqual(*FString::Printf(TEXT("%s: every loaded streaming proxy carries collision"), *Name),
					Case.ProxiesWithCollision, Case.Proxies);
				TestEqual(*FString::Printf(TEXT("%s: a trace at each proxy's centre hits that proxy"), *Name),
					Case.ProxyCentreHits, Case.Proxies);
				TestEqual(*FString::Printf(TEXT("%s: traces on the proxies' shared borders hit"), *Name),
					Case.BorderHits, Case.BorderPoints);
			}
		}
	}

	FString Table = TEXT("Terrain collision, measured:\n");
	for (const FCase& Case : Cases)
	{
		Table += Case.Row() + TEXT("\n");
	}
	AddInfo(Table);
	UE_LOG(LogTemp, Display, TEXT("%s"), *Table);

	if (!StartMap.IsEmpty() && FPackageName::DoesPackageExist(StartMap))
	{
		FEditorFileUtils::LoadMap(StartMap, /*LoadAsTemplate*/ false, /*bShowProgress*/ false);
	}
	else
	{
		GEditor->NewMap(/*bIsPartitionedWorld*/ true);
	}
	return true;
}

#endif // WITH_EDITOR && WITH_DEV_AUTOMATION_TESTS
