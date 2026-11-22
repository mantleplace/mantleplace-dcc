// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportNaming.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceImportNamingTest,
    "MantlePlace.Import.Naming",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportNamingTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceImportNaming;

	const FString Identity = TEXT("a1b2c3d4e5f6a7b8");

	{
		const FString OrderId = TEXT("ord-11112222-3333");
		const FString ManifestSha = TEXT("deadbeefcafef00ddeadbeefcafef00ddeadbeefcafef00ddeadbeefcafef00d");

		TestEqual(TEXT("the order wins when the bundle names one"),
			ResolveIdentity(OrderId, ManifestSha), OrderId);

		TestEqual(TEXT("no order falls back to the manifest hash"),
			ResolveIdentity(FString(), ManifestSha), ManifestSha);

		TestEqual(TEXT("an unusable order still falls back"),
			ResolveIdentity(TEXT("../.."), ManifestSha), ManifestSha);

		TestTrue(TEXT("neither usable yields nothing"),
			ResolveIdentity(FString(), FString()).IsEmpty());

		TestEqual(TEXT("two bundles of one order share an identity regardless of build"),
			ResolveIdentity(OrderId, TEXT("1111111111111111111111111111111111111111111111111111111111111111")),
			ResolveIdentity(OrderId, TEXT("2222222222222222222222222222222222222222222222222222222222222222")));
	}

	{
		TestTrue(TEXT("a hex digest is usable"), IsUsableIdentity(TEXT("a1b2c3d4e5f6")));
		TestTrue(TEXT("a dashed order id is usable"), IsUsableIdentity(TEXT("ord-1111-2222")));
		TestTrue(TEXT("underscores are usable"), IsUsableIdentity(TEXT("order_11112222")));

		TestFalse(TEXT("empty is refused"), IsUsableIdentity(FString()));
		TestFalse(TEXT("too short is refused"), IsUsableIdentity(TEXT("abc")));
		TestFalse(TEXT("a parent traversal is refused"), IsUsableIdentity(TEXT("../../etc")));
		TestFalse(TEXT("a slash is refused"), IsUsableIdentity(TEXT("aaaaaaaa/bbbb")));
		TestFalse(TEXT("a backslash is refused"), IsUsableIdentity(TEXT("aaaaaaaa\\bbbb")));
		TestFalse(TEXT("a dot is refused"), IsUsableIdentity(TEXT("aaaaaaaa.bbbb")));
		TestFalse(TEXT("whitespace is refused"), IsUsableIdentity(TEXT("aaaaaaaa bbbb")));
		TestFalse(TEXT("a colon is refused"), IsUsableIdentity(TEXT("C:aaaaaaaa")));
	}

	{
		TestTrue(TEXT("the default is usable"), IsUsableContentRoot(DefaultContentRoot()));
		TestTrue(TEXT("a studio root is usable"), IsUsableContentRoot(TEXT("/Game/Studio/Geo")));

		TestFalse(TEXT("empty is refused"), IsUsableContentRoot(FString()));
		TestFalse(TEXT("a bare slash is refused"), IsUsableContentRoot(TEXT("/")));
		TestFalse(TEXT("no mount point is refused"), IsUsableContentRoot(TEXT("Game/MantlePlace")));
		TestFalse(TEXT("a trailing slash is refused"), IsUsableContentRoot(TEXT("/Game/MantlePlace/")));
		TestFalse(TEXT("a doubled slash is refused"), IsUsableContentRoot(TEXT("/Game//MantlePlace")));
		TestFalse(TEXT("a parent traversal is refused"), IsUsableContentRoot(TEXT("/Game/../Engine")));

		TestEqual(TEXT("an unusable configured root falls back to the default"),
			ResolveContentRoot(TEXT("/")), FString(DefaultContentRoot()));
		TestEqual(TEXT("an empty setting falls back to the default"),
			ResolveContentRoot(FString()), FString(DefaultContentRoot()));
		TestEqual(TEXT("a usable configured root is honoured"),
			ResolveContentRoot(TEXT("/Game/Studio/Geo")), FString(TEXT("/Game/Studio/Geo")));
	}

	{
		TestEqual(TEXT("outliner folder"),
			OutlinerFolder(Identity), FString(TEXT("MantlePlace/a1b2c3d4")));

		TestTrue(TEXT("the outliner folder does not follow the content root"),
			OutlinerFolder(Identity).StartsWith(TEXT("MantlePlace/")));

		TestEqual(TEXT("import tag"),
			ImportTag(Identity), FString(TEXT("mantleplace_import=a1b2c3d4e5f6a7b8")));
		TestTrue(TEXT("the tag starts with the prefix"),
			ImportTag(Identity).StartsWith(ImportTagPrefix(), ESearchCase::CaseSensitive));
		TestNotEqual(TEXT("two identities that share a FOLDER do not share a TAG"),
			ImportTag(TEXT("a1b2c3d4-one")), ImportTag(TEXT("a1b2c3d4-two")));
		TestEqual(TEXT("but they do share a folder, which is why the tag carries the full id"),
			OutlinerFolder(TEXT("a1b2c3d4-one")), OutlinerFolder(TEXT("a1b2c3d4-two")));
	}

	{
		TestEqual(TEXT("identity truncates to eight"), ShortIdentity(Identity), FString(TEXT("a1b2c3d4")));
		TestEqual(TEXT("a short identity survives whole"), ShortIdentity(TEXT("abc")), FString(TEXT("abc")));
		TestEqual(TEXT("an empty identity stays empty"), ShortIdentity(FString()), FString());
	}

	{
		TestEqual(TEXT("default content root"),
			FString(DefaultContentRoot()), FString(TEXT("/Game/MantlePlace")));

		const FString Root = ImportRoot(DefaultContentRoot(), Identity);
		TestEqual(TEXT("import root"), Root, FString(TEXT("/Game/MantlePlace/a1b2c3d4")));

		TestEqual(TEXT("a configured root is used as given"),
			ImportRoot(TEXT("/Game/Studio/Geo"), Identity),
			FString(TEXT("/Game/Studio/Geo/a1b2c3d4")));

		TestEqual(TEXT("imagery"), SubfolderPath(Root, ESubfolder::Imagery),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/Imagery")));
		TestEqual(TEXT("mesh"), SubfolderPath(Root, ESubfolder::Mesh),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/Mesh")));
		TestEqual(TEXT("buildings"), SubfolderPath(Root, ESubfolder::Buildings),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/Buildings")));
		TestEqual(TEXT("coverage rasters"), SubfolderPath(Root, ESubfolder::CoverageRasters),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/CoverageRasters")));
		TestEqual(TEXT("landcover"), SubfolderPath(Root, ESubfolder::Landcover),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/Landcover")));

		const ESubfolder All[] = { ESubfolder::Imagery, ESubfolder::Mesh, ESubfolder::Buildings,
		                           ESubfolder::CoverageRasters, ESubfolder::Landcover };
		for (const ESubfolder Sub : All)
		{
			const FString Path = SubfolderPath(Root, Sub);
			TestTrue(TEXT("subfolder is beneath the import root"), Path.StartsWith(Root + TEXT("/")));
			TestTrue(TEXT("subfolder is not the import root itself"), Path.Len() > Root.Len() + 1);
		}
	}

	{
		TestEqual(TEXT("object path inside a package directory"),
			ObjectPathIn(TEXT("/Game/MantlePlace/a1b2c3d4/Imagery"), TEXT("MI_Drape_a1b2c3d4")),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/Imagery/MI_Drape_a1b2c3d4.MI_Drape_a1b2c3d4")));

		TestEqual(TEXT("object path of an already-full package name"),
			ObjectPathOf(TEXT("/Game/MantlePlace/a1b2c3d4/Landcover/LI_water"), TEXT("LI_water")),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/Landcover/LI_water.LI_water")));

		TestEqual(TEXT("the two object-path forms agree"),
			ObjectPathIn(TEXT("/Game/X"), TEXT("T_Y")),
			ObjectPathOf(TEXT("/Game/X/T_Y"), TEXT("T_Y")));
	}

	{
		TestEqual(TEXT("texture from a source file"),
			TextureName(TEXT("C:/tmp/import/Imagery/Imagery.png")), FString(TEXT("T_Imagery")));
		TestEqual(TEXT("static mesh from a source file"),
			StaticMeshName(TEXT("C:/tmp/import/Mesh/Terrain.glb")), FString(TEXT("SM_Terrain")));
		TestEqual(TEXT("buildings mesh from a source file"),
			StaticMeshName(TEXT("C:/tmp/import/Buildings/Buildings.glb")), FString(TEXT("SM_Buildings")));

		TestEqual(TEXT("an already-prefixed texture is not prefixed twice"),
			TextureName(TEXT("/tmp/T_Imagery.png")), FString(TEXT("T_Imagery")));
		TestEqual(TEXT("an already-prefixed mesh is not prefixed twice"),
			StaticMeshName(TEXT("/tmp/SM_Terrain.glb")), FString(TEXT("SM_Terrain")));

		TestEqual(TEXT("backslashes resolve the same as forward slashes"),
			TextureName(TEXT("C:\\tmp\\Imagery\\Imagery.png")), TextureName(TEXT("C:/tmp/Imagery/Imagery.png")));

		TestEqual(TEXT("drape material instance"),
			DrapeMaterialName(), FString(TEXT("MI_Drape")));
		TestEqual(TEXT("tree points table"),
			TreePointsTableName(), FString(TEXT("DT_TreePoints")));
		TestEqual(TEXT("foliage graph"), FoliageGraphName(), FString(TEXT("PCG_TreeScatter")));
		TestEqual(TEXT("tree points row"), TreePointsRowName(0), FString(TEXT("Tree_0")));
		TestEqual(TEXT("tree points row, later"), TreePointsRowName(1234), FString(TEXT("Tree_1234")));
	}

	{
		TestEqual(TEXT("layer info asset is prefixed"),
			LayerInfoName(TEXT("water")), FString(TEXT("LI_water")));
		TestEqual(TEXT("layer info preserves the platform's case"),
			LayerInfoName(TEXT("built_up")), FString(TEXT("LI_built_up")));

		const TCHAR* const Materials[] = { TEXT("water"), TEXT("grass"), TEXT("forest"), TEXT("dirt"),
		                                   TEXT("rock"), TEXT("sand"), TEXT("snow"), TEXT("built") };
		for (const TCHAR* const Material : Materials)
		{
			const FString Asset = LayerInfoName(Material);
			TestEqual(TEXT("layer info is exactly LI_ plus the material, verbatim"),
				Asset, FString(TEXT("LI_")) + Material);
			TestTrue(TEXT("the material survives unaltered inside the asset name"),
				Asset.EndsWith(Material, ESearchCase::CaseSensitive));
		}
	}

	{
		TestEqual(TEXT("landscape label"),
			ActorLabel(EActorKind::Landscape, Identity), FString(TEXT("MP_Landscape_a1b2c3d4")));
		TestEqual(TEXT("mesh label"),
			ActorLabel(EActorKind::Mesh, Identity), FString(TEXT("MP_Mesh_a1b2c3d4")));
		TestEqual(TEXT("buildings label"),
			ActorLabel(EActorKind::Buildings, Identity), FString(TEXT("MP_Buildings_a1b2c3d4")));
		TestEqual(TEXT("foliage label"),
			ActorLabel(EActorKind::Foliage, Identity), FString(TEXT("MP_Foliage_a1b2c3d4")));

		TestEqual(TEXT("Extent box label (no identity: it belongs to no import)"),
			ExtentBoxLabel(), FString(TEXT("MP_ExtentBox")));

		TestEqual(TEXT("road spline prefix"),
			RoadSplineLabelPrefix(Identity), FString(TEXT("MP_RoadSpline_a1b2c3d4_")));
		TestEqual(TEXT("road spline label"),
			RoadSplineLabel(Identity, 0), FString(TEXT("MP_RoadSpline_a1b2c3d4_000")));
		TestEqual(TEXT("road spline label, zero padded"),
			RoadSplineLabel(Identity, 7), FString(TEXT("MP_RoadSpline_a1b2c3d4_007")));
		TestEqual(TEXT("road spline label past the padding width"),
			RoadSplineLabel(Identity, 1234), FString(TEXT("MP_RoadSpline_a1b2c3d4_1234")));

		const FString Prefix = RoadSplineLabelPrefix(Identity);
		for (const int32 Index : { 0, 1, 9, 10, 99, 100, 999, 1000 })
		{
			TestTrue(TEXT("every road spline label starts with the prefix"),
				RoadSplineLabel(Identity, Index).StartsWith(Prefix, ESearchCase::CaseSensitive));
		}

		TestEqual(TEXT("public record label"),
			PublicRecordLabel(Identity, TEXT("parcels")), FString(TEXT("MP_Record_Parcels_a1b2c3d4")));
		TestEqual(TEXT("public record label, a family with words"),
			PublicRecordLabel(Identity, TEXT("protected_areas")), FString(TEXT("MP_Record_ProtectedAreas_a1b2c3d4")));
		TestEqual(TEXT("public record label, punctuation dropped"),
			PublicRecordLabel(Identity, TEXT("land-use/2")), FString(TEXT("MP_Record_LandUse2_a1b2c3d4")));
		TestEqual(TEXT("public record label, no family"),
			PublicRecordLabel(Identity, FString()), FString(TEXT("MP_Record_Records_a1b2c3d4")));

		const FString Labels[] = { ActorLabel(EActorKind::Landscape, Identity),
		                           ActorLabel(EActorKind::Mesh, Identity),
		                           ActorLabel(EActorKind::Buildings, Identity),
		                           ActorLabel(EActorKind::Foliage, Identity) };
		for (int32 I = 0; I < UE_ARRAY_COUNT(Labels); ++I)
		{
			for (int32 J = I + 1; J < UE_ARRAY_COUNT(Labels); ++J)
			{
				TestNotEqual(TEXT("actor labels are distinct"), Labels[I], Labels[J]);
				TestFalse(TEXT("no actor label prefixes another"), Labels[J].StartsWith(Labels[I]));
				TestFalse(TEXT("no actor label prefixes another, reversed"), Labels[I].StartsWith(Labels[J]));
			}
			TestFalse(TEXT("a road spline label is not one of the single-actor labels"),
				Labels[I].StartsWith(Prefix));
		}

		TestNotEqual(TEXT("different identities give different labels"),
			ActorLabel(EActorKind::Landscape, TEXT("a1b2c3d4")),
			ActorLabel(EActorKind::Landscape, TEXT("99887766")));

		TestEqual(TEXT("identities sharing a short form share a label — the caller must guard"),
			ActorLabel(EActorKind::Landscape, TEXT("a1b2c3d4-one")),
			ActorLabel(EActorKind::Landscape, TEXT("a1b2c3d4-two")));
	}

	return true;
}

#endif
