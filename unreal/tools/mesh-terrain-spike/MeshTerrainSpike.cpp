// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

// Mesh Terrain spike harness: a scratch module, never part of the plugin.
// Every scenario is parameterised by environment variables and logs "SPIKE" lines.

#include "Modules/ModuleManager.h"
IMPLEMENT_MODULE(FDefaultModuleImpl, MeshTerrainSpike);

#if WITH_DEV_AUTOMATION_TESTS && WITH_EDITOR

#include "CQTest.h"
#include "EditorWorldUtils.h"
#include "EngineUtils.h"
#include "HAL/PlatformMemory.h"
#include "HAL/PlatformTime.h"
#include "IImageWrapper.h"
#include "IImageWrapperModule.h"
#include "Misc/FileHelper.h"
#include "Tests/AutomationEditorCommon.h"
#include "Test/MeshPartitionTestUtils.h"

#include "MeshPartition.h"
#include "MeshPartitionDefinition.h"
#include "MeshPartitionEditorComponent.h"
#include "MeshPartitionHeightmapImporter.h"
#include "MeshPartitionChannelCollection.h"
#include "MeshPartitionMaterialExpressionUtils.h"
#include "Modifiers/MeshPartitionMeshProvider.h"

#include "AssetImportTask.h"
#include "AssetToolsModule.h"
#include "IAssetTools.h"
#include "Engine/StaticMesh.h"
#include "Engine/StaticMeshActor.h"
#include "MeshDescriptionToDynamicMesh.h"
#include "DynamicMesh/DynamicMesh3.h"
#include "DynamicMesh/DynamicMeshAttributeSet.h"
#include "DynamicMesh/MeshNormals.h"
#include "MaterialEditingLibrary.h"
#include "Materials/Material.h"
#include "MaterialShared.h"
#include "RHIGlobals.h"
#include "Materials/MaterialExpressionLinearInterpolate.h"
#include "Materials/MaterialExpressionConstant3Vector.h"

using namespace UE::MeshPartition;
using namespace UE::Geometry;

namespace Spike
{
	FString Env(const TCHAR* Name, const TCHAR* Default = TEXT(""))
	{
		const FString V = FPlatformMisc::GetEnvironmentVariable(Name);
		return V.IsEmpty() ? FString(Default) : V;
	}
	double EnvD(const TCHAR* Name, double Default = 0.0)
	{
		const FString V = Env(Name);
		return V.IsEmpty() ? Default : FCString::Atod(*V);
	}
	double MemGB() { return FPlatformMemory::GetStats().UsedPhysical / (1024.0 * 1024.0 * 1024.0); }
	double PeakGB() { return FPlatformMemory::GetStats().PeakUsedPhysical / (1024.0 * 1024.0 * 1024.0); }

	bool LoadPng16(const FString& Path, TArray<uint16>& Out, int32& W, int32& H)
	{
		TArray64<uint8> Bytes;
		if (!FFileHelper::LoadFileToArray(Bytes, *Path)) return false;
		IImageWrapperModule& M = FModuleManager::LoadModuleChecked<IImageWrapperModule>("ImageWrapper");
		TSharedPtr<IImageWrapper> Wr = M.CreateImageWrapper(EImageFormat::PNG);
		if (!Wr->SetCompressed(Bytes.GetData(), Bytes.Num()) || Wr->GetBitDepth() != 16) return false;
		W = Wr->GetWidth(); H = Wr->GetHeight();
		TArray64<uint8> Raw;
		if (!Wr->GetRaw(ERGBFormat::Gray, 16, Raw)) return false;
		Out.SetNumUninitialized(int64(W) * H);
		FMemory::Memcpy(Out.GetData(), Raw.GetData(), Out.Num() * 2);
		return true;
	}

	/** The placement the Landscape path gives the same bundle: North -> +X, East -> +Y, AOI centred on the
	 *  origin, z = z_min + u/65535 * range. All values come from the manifest via the environment. */
	struct FFrame
	{
		int32 R = 0;            // samples per edge
		double EastCm = 0, NorthCm = 0, ZMinCm = 0, ZRangeCm = 0;
		double SE() const { return EastCm / (R - 1); }
		double SN() const { return NorthCm / (R - 1); }
	};

	double Bilinear(const TArray<uint16>& Hm, int32 R, double Row, double Col)
	{
		Row = FMath::Clamp(Row, 0.0, double(R - 1)); Col = FMath::Clamp(Col, 0.0, double(R - 1));
		const int32 R0 = FMath::Min(int32(Row), R - 2), C0 = FMath::Min(int32(Col), R - 2);
		const double dr = Row - R0, dc = Col - C0;
		auto S = [&](int32 r, int32 c) { return double(Hm[int64(r) * R + c]); };
		return (S(R0, C0) * (1 - dc) + S(R0, C0 + 1) * dc) * (1 - dr) + (S(R0 + 1, C0) * (1 - dc) + S(R0 + 1, C0 + 1) * dc) * dr;
	}

	/** Compare every Nth vertex of every section against the Landscape-equivalent surface. */
	void Measure(UWorld* World, AMeshPartition* Mega, const TArray<uint16>& Hm, const FFrame& F, const TCHAR* Tag)
	{
		int64 Verts = 0, Checked = 0, Sections = 0;
		double MaxMis = 0, MaxDz = 0, SumDz2 = 0;
		FVector MinW(DBL_MAX), MaxW(-DBL_MAX);
		for (TActorIterator<AActor> It(World); It; ++It)
		{
			UMeshProviderModifier* P = It->FindComponentByClass<UMeshProviderModifier>();
			if (!P || !P->GetMesh()) continue;
			++Sections;
			const FTransform T = P->GetComponentTransform();
			const FDynamicMesh3& M = *P->GetMesh();
			Verts += M.VertexCount();
			int32 i = 0;
			for (int32 Vid : M.VertexIndicesItr())
			{
				const FVector W = T.TransformPosition(M.GetVertex(Vid));
				MinW = MinW.ComponentMin(W); MaxW = MaxW.ComponentMax(W);
				if ((i++ % 7) != 0) continue;
				const double Row = (F.R - 1) - (W.X + F.NorthCm / 2) / F.SN();
				const double Col = (W.Y + F.EastCm / 2) / F.SE();
				MaxMis = FMath::Max(MaxMis, FMath::Max(FMath::Abs(Row - FMath::RoundToDouble(Row)), FMath::Abs(Col - FMath::RoundToDouble(Col))));
				const double Zexp = F.ZMinCm + Bilinear(Hm, F.R, Row, Col) / 65535.0 * F.ZRangeCm;
				const double Dz = W.Z - Zexp;
				MaxDz = FMath::Max(MaxDz, FMath::Abs(Dz)); SumDz2 += Dz * Dz; ++Checked;
			}
		}
		UE_LOG(LogTemp, Display, TEXT("SPIKE %s sections=%lld vertices=%lld checked=%lld max_misregistration_samples=%.4f max_dz_cm=%.3f rms_dz_cm=%.3f bounds_min=%s bounds_max=%s"),
			Tag, Sections, Verts, Checked, MaxMis, MaxDz, Checked ? FMath::Sqrt(SumDz2 / Checked) : 0.0, *MinW.ToString(), *MaxW.ToString());
	}

	/** Build sections ourselves with vertices exactly on the samples (the corner-registered grid). */
	AMeshPartition* BuildGrid(UWorld* World, const TArray<uint16>& Hm, const FFrame& F, int32 NSec, const FTransform& MegaXf, bool bChannels)
	{
		AMeshPartition* Mega = World->SpawnActor<AMeshPartition>(AMeshPartition::StaticClass(), FTransform::Identity);
		UMeshPartitionEditorComponent* Ed = NewObject<UMeshPartitionEditorComponent>(Mega, UMeshPartitionEditorComponent::StaticClass(), TEXT("MegaMeshEditorComponent"), RF_Transactional);
		Mega->SetMeshPartitionComponent(Ed);
		const int32 Q = F.R - 1;
		TArray<FBox> Bounds;
		for (int32 sy = 0; sy < NSec; ++sy)
		for (int32 sx = 0; sx < NSec; ++sx)
		{
			const int32 c0 = Q * sx / NSec, c1 = Q * (sx + 1) / NSec, r0 = Q * sy / NSec, r1 = Q * (sy + 1) / NSec;
			// Importer-local frame (pre-yaw): +X east (columns), +Y south (rows), the same frame FHeightmapImporter uses.
			const FVector Centre(-F.EastCm / 2 + (c0 + c1) * 0.5 * F.SE(), -F.NorthCm / 2 + (r0 + r1) * 0.5 * F.SN(), 0);
			FDynamicMesh3 M;
			M.EnableAttributes();
			FDynamicMeshUVOverlay* UV = M.Attributes()->PrimaryUV();
			const int32 Wd = c1 - c0 + 1;
			for (int32 r = r0; r <= r1; ++r)
			for (int32 c = c0; c <= c1; ++c)
			{
				const FVector3d P(-F.EastCm / 2 + c * F.SE() - Centre.X, -F.NorthCm / 2 + r * F.SN() - Centre.Y, Hm[int64(r) * F.R + c] / 65535.0 * F.ZRangeCm);
				M.AppendVertex(P);
				UV->AppendElement(FVector2f(float(c) / Q, float(r) / Q));
			}
			for (int32 r = 0; r < r1 - r0; ++r)
			for (int32 c = 0; c < c1 - c0; ++c)
			{
				const int32 a = r * Wd + c, b = a + 1, d = a + Wd, e = d + 1;
				const int32 t0 = M.AppendTriangle(a, e, b); UV->SetTriangle(t0, FIndex3i(a, e, b));
				const int32 t1 = M.AppendTriangle(a, d, e); UV->SetTriangle(t1, FIndex3i(a, d, e));
			}
			FMeshNormals::InitializeOverlayToPerVertexNormals(M.Attributes()->PrimaryNormals());
			if (bChannels)
			{
				// Demonstration values only (four column bands): the real values would be the bundle's
				// published material_weights, resampled onto the grid exactly as the Landscape path does.
				M.Attributes()->SetNumWeightLayers(4);
				for (int32 k = 0; k < 4; ++k)
				{
					FDynamicMeshWeightAttribute* L = M.Attributes()->GetWeightLayer(k);
					L->SetName(*FString::Printf(TEXT("band_%d"), k));
					int32 v = 0;
					for (int32 r = r0; r <= r1; ++r)
					for (int32 c = c0; c <= c1; ++c, ++v)
					{
						const float Val = (c * 4 / F.R) == k ? 1.f : 0.f;
						L->SetValue(v, &Val);
					}
				}
			}
			AActor* Base = Ed->SpawnBaseModifier(MoveTemp(M), {}, FTransform(Centre)); // relative: sections follow the MegaMesh actor
			if (UMeshProviderModifier* P = Base->FindComponentByClass<UMeshProviderModifier>()) Bounds.Append(P->ComputeBounds());
		}
		Mega->SetActorTransform(MegaXf);
		Ed->OnBoundsChanged(Bounds, EChangeType::StateChange);
		return Mega;
	}
}

TEST_CLASS_WITH_FLAGS(MeshTerrainSpike, "Spike", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
{
	TUniquePtr<FScopedEditorWorld> ScopedEditorWorld;
	TOptional<TestUtils::FScopedCVarOverride> DisableDDCWrite, DisableDDCRead;
	UWorld* World = nullptr;
	AMeshPartition* Mega = nullptr;
	UMeshPartitionEditorComponent* Ed = nullptr;
	double T0 = 0, Mem0 = 0;

	BEFORE_EACH()
	{
		FWorldInitializationValues IV;
		IV.RequiresHitProxies(false); IV.ShouldSimulatePhysics(false); IV.EnableTraceCollision(false);
		IV.CreateNavigation(false); IV.CreateAISystem(false); IV.AllowAudioPlayback(false);
		IV.CreatePhysicsScene(true); IV.CreateWorldPartition(true);
		ScopedEditorWorld = FAutomationEditorCommonUtils::CreateScopedEditorWorld(TEXT("/MeshPartition/UnitTests/MeshPartitionUnitTests"), IV);
		ASSERT_THAT(IsNotNull(ScopedEditorWorld.Get()));
		World = ScopedEditorWorld->GetWorld();
		DisableDDCWrite.Emplace(TEXT("MegaMesh.Preview.EnableDDCWrite"), TEXT("0"));
		DisableDDCRead.Emplace(TEXT("MegaMesh.Preview.EnableDDCRead"), TEXT("0"));
		ASSERT_THAT(IsTrue(TestUtils::LoadWorldPartitionRegion(World)));
		UE_LOG(LogTemp, Display, TEXT("SPIKE world_partition=%d"), World->GetWorldPartition() != nullptr);
	}

	TEST_METHOD(Run)
	{
		TestCommandBuilder
			.Do(TEXT("Import"), [this]()
			{
				const FString Mode = Spike::Env(TEXT("SPIKE_MODE"), TEXT("importer"));
				Spike::FFrame F;
				F.EastCm = Spike::EnvD(TEXT("SPIKE_EAST_CM")); F.NorthCm = Spike::EnvD(TEXT("SPIKE_NORTH_CM"));
				F.ZMinCm = Spike::EnvD(TEXT("SPIKE_ZMIN_CM")); F.ZRangeCm = Spike::EnvD(TEXT("SPIKE_ZRANGE_CM"));
				const int32 NSec = int32(Spike::EnvD(TEXT("SPIKE_SECTIONS"), 16));
				// The Landscape path's frame: AOI centred on the origin; the mesh path's yaw +90 maps the
				// importer's (East, South) onto the world's (North, East).
				const FTransform MegaXf(FRotator(0, 90, 0), FVector(0, 0, F.ZMinCm));
				TArray<uint16> Hm; int32 W = 0, H = 0;

				Mem0 = Spike::MemGB();
				T0 = FPlatformTime::Seconds();
				if (Mode == TEXT("convert"))
				{
					UAssetImportTask* Task = NewObject<UAssetImportTask>();
					Task->Filename = Spike::Env(TEXT("SPIKE_GLB"));
					Task->DestinationPath = TEXT("/Game/Spike");
					Task->bAutomated = true; Task->bSave = false; Task->bReplaceExisting = true;
					FAssetToolsModule::GetModule().Get().ImportAssetTasks({ Task });
					const double TImport = FPlatformTime::Seconds() - T0;
					TArray<UStaticMesh*> Meshes;
					for (UObject* O : Task->GetObjects()) if (UStaticMesh* S = Cast<UStaticMesh>(O)) Meshes.Add(S);
					UE_LOG(LogTemp, Display, TEXT("SPIKE convert glb_static_meshes=%d glb_import_s=%.2f"), Meshes.Num(), TImport);
					ASSERT_THAT(IsTrue(Meshes.Num() > 0));
					const double T1 = FPlatformTime::Seconds();
					Mega = World->SpawnActor<AMeshPartition>(AMeshPartition::StaticClass(), FTransform::Identity);
					Ed = NewObject<UMeshPartitionEditorComponent>(Mega, UMeshPartitionEditorComponent::StaticClass(), TEXT("MegaMeshEditorComponent"), RF_Transactional);
					Mega->SetMeshPartitionComponent(Ed);
					// The glb's placement: centroid-local, z=0 at centroid ground -> (0,0,ground), yaw +90.
					const FTransform GlbXf(FRotator(0, 90, 0), FVector(0, 0, Spike::EnvD(TEXT("SPIKE_GROUND_CM"))));
					TArray<FBox> Bounds; int64 V = 0, Tri = 0; FBox Expected(ForceInit);
					for (UStaticMesh* S : Meshes)
					{
						FDynamicMesh3 D;
						FMeshDescriptionToDynamicMesh Conv;
						Conv.Convert(S->GetMeshDescription(0), D);
						V += D.VertexCount(); Tri += D.TriangleCount();
						Expected += S->GetBoundingBox().TransformBy(GlbXf);
						// What the Convert tool does on Accept: recentre each section on its bounds.
						const FVector C = FBox(D.GetBounds()).GetCenter();
						for (int32 Vid : D.VertexIndicesItr()) D.SetVertex(Vid, D.GetVertex(Vid) - C);
						TArray<UMaterialInterface*> Mats;
						for (const FStaticMaterial& SMat : S->GetStaticMaterials()) Mats.Add(SMat.MaterialInterface);
						AActor* Base = Ed->SpawnBaseModifier(MoveTemp(D), Mats, FTransform(C));
						if (UMeshProviderModifier* P = Base->FindComponentByClass<UMeshProviderModifier>()) Bounds.Append(P->ComputeBounds());
					}
					Mega->SetActorTransform(GlbXf);
					Ed->OnBoundsChanged(Bounds, EChangeType::StateChange);
					FBox Got(ForceInit);
					for (TActorIterator<AActor> It(World); It; ++It)
						if (UMeshProviderModifier* P = It->FindComponentByClass<UMeshProviderModifier>()) Got += P->Bounds.GetBox();
					UE_LOG(LogTemp, Display, TEXT("SPIKE convert vertices=%lld triangles=%lld convert_s=%.2f mem_delta_gb=%.2f expected_bounds=%s got_bounds=%s"),
						V, Tri, FPlatformTime::Seconds() - T1, Spike::MemGB() - Mem0, *Expected.ToString(), *Got.ToString());
					return;
				}

				ASSERT_THAT(IsTrue(Spike::LoadPng16(Spike::Env(TEXT("SPIKE_PNG")), Hm, W, H) && W == H));
				F.R = W;
				UE_LOG(LogTemp, Display, TEXT("SPIKE heightmap=%dx%d decode_s=%.2f"), W, H, FPlatformTime::Seconds() - T0);
				T0 = FPlatformTime::Seconds();

				if (Mode == TEXT("importer"))
				{
					FHeightmapImportParams Params;
					Params.World = World;
					Params.HeightmapFilename = Spike::Env(TEXT("SPIKE_PNG"));
					Params.MeshResolution = FInt32Point(W - 1); // quads, so vertices == samples
					Params.MeshSize = FVector(F.EastCm, F.NorthCm, F.ZRangeCm);
					Params.SectionsResolution = FInt32Point(NSec);
					Params.bSaveAndUnload = false; // the plugin never saves what it imports
					Params.LocationVolumesResolution = FInt32Point(0);
					FHeightmapImporter Importer(Params);
					const bool bOk = Importer.Import(nullptr);
					UE_LOG(LogTemp, Display, TEXT("SPIKE importer ok=%d error=\"%s\""), bOk, *Importer.GetErrorText().ToString());
					ASSERT_THAT(IsTrue(bOk));
					Mega = Importer.GetMegaMesh();
					UE_LOG(LogTemp, Display, TEXT("SPIKE importer_s=%.2f mem_delta_gb=%.2f peak_gb=%.2f"), FPlatformTime::Seconds() - T0, Spike::MemGB() - Mem0, Spike::PeakGB());
					Spike::Measure(World, Mega, Hm, F, TEXT("importer_at_identity"));
					Mega->SetActorTransform(MegaXf);
					Spike::Measure(World, Mega, Hm, F, TEXT("importer_placed"));
				}
				else
				{
					const bool bChannels = Spike::Env(TEXT("SPIKE_CHANNELS")) == TEXT("1");
					Mega = Spike::BuildGrid(World, Hm, F, NSec, MegaXf, bChannels);
					UE_LOG(LogTemp, Display, TEXT("SPIKE grid_s=%.2f mem_delta_gb=%.2f peak_gb=%.2f"), FPlatformTime::Seconds() - T0, Spike::MemGB() - Mem0, Spike::PeakGB());
					Spike::Measure(World, Mega, Hm, F, TEXT("grid_placed"));
					if (bChannels)
					{
						// Channels are declared on the definition, whose ChannelMap.ChannelDescs is private with no
						// setter: written here through reflection on a transient copy of the default definition.
						UMeshPartitionDefinition* Def = DuplicateObject(const_cast<UMeshPartitionDefinition*>(UMeshPartitionDefinition::GetDefaultMegaMeshDefinition()), GetTransientPackage());
						FArrayProperty* DescsProp = FindFProperty<FArrayProperty>(FChannelMap::StaticStruct(), TEXT("ChannelDescs"));
						FScriptArrayHelper Arr(DescsProp, DescsProp->ContainerPtrToValuePtr<void>(&Def->GetChannelMap()));
						for (int32 k = 0; k < 4; ++k)
						{
							const int32 Idx = Arr.AddValue();
							reinterpret_cast<FChannelDesc*>(Arr.GetRawPtr(Idx))->Name = *FString::Printf(TEXT("band_%d"), k);
						}
						// A Mesh Terrain material built programmatically: Resource -> ChannelSample per band -> lerp chain.
						UMaterial* Mat = NewObject<UMaterial>(GetTransientPackage(), TEXT("M_SpikeBands"));
						auto* Res = Cast<UMaterialExpressionMeshPartitionResource>(UMaterialEditingLibrary::CreateMaterialExpression(Mat, UMaterialExpressionMeshPartitionResource::StaticClass()));
						UMaterialExpression* Prev = UMaterialEditingLibrary::CreateMaterialExpression(Mat, UMaterialExpressionConstant3Vector::StaticClass());
						for (int32 k = 0; k < 4; ++k)
						{
							auto* S = Cast<UMaterialExpressionMeshPartitionChannelSample>(UMaterialEditingLibrary::CreateMaterialExpression(Mat, UMaterialExpressionMeshPartitionChannelSample::StaticClass()));
							S->Channel = *FString::Printf(TEXT("band_%d"), k);
							S->MeshPartitionDefinition = Def;
							S->ChannelTextureInput.Connect(0, Res);
							auto* Col = Cast<UMaterialExpressionConstant3Vector>(UMaterialEditingLibrary::CreateMaterialExpression(Mat, UMaterialExpressionConstant3Vector::StaticClass()));
							Col->Constant = FLinearColor(k == 0, k == 1, k == 2);
							auto* L = Cast<UMaterialExpressionLinearInterpolate>(UMaterialEditingLibrary::CreateMaterialExpression(Mat, UMaterialExpressionLinearInterpolate::StaticClass()));
							L->A.Connect(0, Prev); L->B.Connect(0, Col); L->Alpha.Connect(0, S);
							Prev = L;
						}
						UMaterialEditingLibrary::ConnectMaterialProperty(Prev, TEXT(""), MP_BaseColor);
						Mat->PreEditChange(nullptr); Mat->PostEditChange();
						TArray<FString> Errors;
						if (FMaterialResource* MR = Mat->GetMaterialResource(GMaxRHIShaderPlatform)) { MR->FinishCompilation(); Errors = MR->GetCompileErrors(); }
						FProperty* MatProp = FindFProperty<FProperty>(UMeshPartitionDefinition::StaticClass(), TEXT("Material"));
						*MatProp->ContainerPtrToValuePtr<TObjectPtr<UMaterialInterface>>(Def) = Mat;
						Mega->SetMeshPartitionDefinition(Def);
						int32 ActiveSum = 0;
						for (TActorIterator<AActor> It(World); It; ++It)
							if (UMeshProviderModifier* P = It->FindComponentByClass<UMeshProviderModifier>())
							{
								// The bake (FChannelTextureRenderer::GenerateSectionMeshChannels) is private; it matches a
								// mesh weight layer to a channel by name, so count the layers the definition will match.
								const FDynamicMeshAttributeSet* A = P->GetMesh()->Attributes();
								for (int32 k = 0; A && k < A->NumWeightLayers(); ++k)
									ActiveSum += Def->GetChannelMap().FindChannel(A->GetWeightLayer(k)->GetName()) != INDEX_NONE;
							}
						UE_LOG(LogTemp, Display, TEXT("SPIKE channels declared=%d find_band_3=%d material_errors=%d first_error=\"%s\" weight_layers_matched_to_channels=%d definition_material=%s"),
							Def->GetChannelMap().GetNumChannels(), Def->GetChannelMap().FindChannel(TEXT("band_3")), Errors.Num(), Errors.Num() ? *Errors[0] : TEXT(""), ActiveSum,
							Def->GetMaterial() ? *Def->GetMaterial()->GetName() : TEXT("none"));
					}
				}
			})
			.Then(TEXT("Preview build"), [this]()
			{
				if (Spike::Env(TEXT("SPIKE_PREVIEW")) != TEXT("1") || Mega == nullptr) return;
				Ed = Cast<UMeshPartitionEditorComponent>(Mega->GetMeshPartitionComponent());
				Ed->SetPauseTransformerPipeline(true);
				T0 = FPlatformTime::Seconds();
				TArray<FBox> Inf = { FBox(FVector(-HALF_WORLD_MAX), FVector(HALF_WORLD_MAX)) };
				Ed->BuildMegaMeshPreviewSections(Inf);
				Ed->Update();
			})
			.Until(TEXT("Preview done"), [this]()
			{
				return Spike::Env(TEXT("SPIKE_PREVIEW")) != TEXT("1") || Ed == nullptr || !Ed->IsAnyPreviewSectionBuildActive();
			}, FTimespan::FromMinutes(60))
			.Then(TEXT("Report"), [this]()
			{
				if (Spike::Env(TEXT("SPIKE_PREVIEW")) == TEXT("1") && Ed)
				{
					int32 Previews = 0;
					for (TActorIterator<AActor> It(World); It; ++It) if (It->GetClass()->GetName().Contains(TEXT("PreviewSection"))) ++Previews;
					UE_LOG(LogTemp, Display, TEXT("SPIKE preview_s=%.2f preview_sections=%d mem_delta_gb=%.2f peak_gb=%.2f"), FPlatformTime::Seconds() - T0, Previews, Spike::MemGB() - Mem0, Spike::PeakGB());
				}
			});
	}
};

#endif
