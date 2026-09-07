// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportNaming.h"

// The exact strings the importer writes into a user's project. They are asserted literally, on
// purpose: this is the one file that says what the naming standard in `unreal/CLAUDE.md` actually
// produces, and a change to any of these strings is a change a user sees in their outliner and
// their Content Browser. If an assertion here has to be edited, that edit IS the decision — take
// it deliberately, and check whether it needs a line in the release body.
//
// Nothing here needs an editor, a bundle or a world, which is why it can be asserted at all: CI
// never compiles this plugin, so a test that needed a running editor would be a test nobody runs.

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FMantlePlaceImportNamingTest,
    "MantlePlace.Import.Naming",
    EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportNamingTest::RunTest(const FString& Parameters)
{
	using namespace MantlePlaceImportNaming;

	// A representative identity: long enough that truncation bites, and hex so it reads like one.
	const FString Identity = TEXT("a1b2c3d4e5f6a7b8");

	// --- Which identity wins ------------------------------------------------------------------
	{
		const FString OrderId = TEXT("ord-11112222-3333");
		const FString ManifestSha = TEXT("deadbeefcafef00ddeadbeefcafef00ddeadbeefcafef00ddeadbeefcafef00d");

		// The order, always, when there is one. It is the only one of the two that survives a
		// rebuild, which is the entire point of ADR 0002.
		TestEqual(TEXT("the order wins when the bundle names one"),
			ResolveIdentity(OrderId, ManifestSha), OrderId);

		// A bundle with no order is a local or admin bundle: there is no order for it to be a
		// rebuild OF, so a content hash is the honest identity.
		TestEqual(TEXT("no order falls back to the manifest hash"),
			ResolveIdentity(FString(), ManifestSha), ManifestSha);

		// An unusable order does not poison the fallback.
		TestEqual(TEXT("an unusable order still falls back"),
			ResolveIdentity(TEXT("../.."), ManifestSha), ManifestSha);

		// Neither usable is an EMPTY identity, which the caller must refuse rather than default.
		TestTrue(TEXT("neither usable yields nothing"),
			ResolveIdentity(FString(), FString()).IsEmpty());

		// The job id is never consulted. Asserted by construction: ResolveIdentity has no parameter
		// for it, and this line is here so that adding one is a visible decision.
		TestEqual(TEXT("two bundles of one order share an identity regardless of build"),
			ResolveIdentity(OrderId, TEXT("1111111111111111111111111111111111111111111111111111111111111111")),
			ResolveIdentity(OrderId, TEXT("2222222222222222222222222222222222222222222222222222222222222222")));
	}

	// --- What may be an identity ----------------------------------------------------------------
	{
		TestTrue(TEXT("a hex digest is usable"), IsUsableIdentity(TEXT("a1b2c3d4e5f6")));
		TestTrue(TEXT("a dashed order id is usable"), IsUsableIdentity(TEXT("ord-1111-2222")));
		TestTrue(TEXT("underscores are usable"), IsUsableIdentity(TEXT("order_11112222")));

		// Each of these becomes a path segment of a directory the importer FORCE-DELETES. They are
		// refused rather than sanitised: rewriting one invents an identity nobody chose, and the
		// rewrite is what would silently point the delete somewhere else.
		TestFalse(TEXT("empty is refused"), IsUsableIdentity(FString()));
		TestFalse(TEXT("too short is refused"), IsUsableIdentity(TEXT("abc")));
		TestFalse(TEXT("a parent traversal is refused"), IsUsableIdentity(TEXT("../../etc")));
		TestFalse(TEXT("a slash is refused"), IsUsableIdentity(TEXT("aaaaaaaa/bbbb")));
		TestFalse(TEXT("a backslash is refused"), IsUsableIdentity(TEXT("aaaaaaaa\\bbbb")));
		TestFalse(TEXT("a dot is refused"), IsUsableIdentity(TEXT("aaaaaaaa.bbbb")));
		TestFalse(TEXT("whitespace is refused"), IsUsableIdentity(TEXT("aaaaaaaa bbbb")));
		TestFalse(TEXT("a colon is refused"), IsUsableIdentity(TEXT("C:aaaaaaaa")));
	}

	// --- What may be a content root -------------------------------------------------------------
	{
		TestTrue(TEXT("the default is usable"), IsUsableContentRoot(DefaultContentRoot()));
		TestTrue(TEXT("a studio root is usable"), IsUsableContentRoot(TEXT("/Game/Studio/Geo")));

		TestFalse(TEXT("empty is refused"), IsUsableContentRoot(FString()));
		TestFalse(TEXT("a bare slash is refused"), IsUsableContentRoot(TEXT("/")));
		TestFalse(TEXT("no mount point is refused"), IsUsableContentRoot(TEXT("Game/MantlePlace")));
		TestFalse(TEXT("a trailing slash is refused"), IsUsableContentRoot(TEXT("/Game/MantlePlace/")));
		TestFalse(TEXT("a doubled slash is refused"), IsUsableContentRoot(TEXT("/Game//MantlePlace")));
		TestFalse(TEXT("a parent traversal is refused"), IsUsableContentRoot(TEXT("/Game/../Engine")));

		// An unusable configured root falls back rather than failing the import. The importer
		// creates and force-deletes a directory beneath this; a malformed root is not a thing to
		// resolve creatively.
		TestEqual(TEXT("an unusable configured root falls back to the default"),
			ResolveContentRoot(TEXT("/")), FString(DefaultContentRoot()));
		TestEqual(TEXT("an empty setting falls back to the default"),
			ResolveContentRoot(FString()), FString(DefaultContentRoot()));
		TestEqual(TEXT("a usable configured root is honoured"),
			ResolveContentRoot(TEXT("/Game/Studio/Geo")), FString(TEXT("/Game/Studio/Geo")));
	}

	// --- Placement in the level -----------------------------------------------------------------
	{
		TestEqual(TEXT("outliner folder"),
			OutlinerFolder(Identity), FString(TEXT("MantlePlace/a1b2c3d4")));

		// Fixed top segment, deliberately: the content root is configurable, so a folder-borne
		// marker would vanish for exactly the studios that setting exists for.
		TestTrue(TEXT("the outliner folder does not follow the content root"),
			OutlinerFolder(Identity).StartsWith(TEXT("MantlePlace/")));

		// The tag carries the FULL identity, not the truncation — it is what re-import matches on,
		// and matching on eight characters would let a colliding order's actors be destroyed.
		TestEqual(TEXT("import tag"),
			ImportTag(Identity), FString(TEXT("mantleplace_import=a1b2c3d4e5f6a7b8")));
		TestTrue(TEXT("the tag starts with the prefix"),
			ImportTag(Identity).StartsWith(ImportTagPrefix(), ESearchCase::CaseSensitive));
		TestNotEqual(TEXT("two identities that share a FOLDER do not share a TAG"),
			ImportTag(TEXT("a1b2c3d4-one")), ImportTag(TEXT("a1b2c3d4-two")));
		TestEqual(TEXT("but they do share a folder, which is why the tag carries the full id"),
			OutlinerFolder(TEXT("a1b2c3d4-one")), OutlinerFolder(TEXT("a1b2c3d4-two")));
	}

	// --- Identity ---------------------------------------------------------------------------
	{
		TestEqual(TEXT("identity truncates to eight"), ShortIdentity(Identity), FString(TEXT("a1b2c3d4")));
		// Shorter than the truncation is returned whole rather than padded — a short identity is
		// still an identity, and padding would invent characters that are not in it.
		TestEqual(TEXT("a short identity survives whole"), ShortIdentity(TEXT("abc")), FString(TEXT("abc")));
		TestEqual(TEXT("an empty identity stays empty"), ShortIdentity(FString()), FString());
	}

	// --- Package paths ----------------------------------------------------------------------
	{
		TestEqual(TEXT("default content root"),
			FString(DefaultContentRoot()), FString(TEXT("/Game/MantlePlace")));

		const FString Root = ImportRoot(DefaultContentRoot(), Identity);
		TestEqual(TEXT("import root"), Root, FString(TEXT("/Game/MantlePlace/a1b2c3d4")));

		// A configured root is honoured verbatim; nothing is prepended or rewritten.
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

		// Every subfolder is strictly BENEATH the import root. The re-import wipe force-deletes the
		// root, so a subfolder that escaped it would be content the wipe silently orphaned.
		const ESubfolder All[] = { ESubfolder::Imagery, ESubfolder::Mesh, ESubfolder::Buildings,
		                           ESubfolder::CoverageRasters, ESubfolder::Landcover };
		for (const ESubfolder Sub : All)
		{
			const FString Path = SubfolderPath(Root, Sub);
			TestTrue(TEXT("subfolder is beneath the import root"), Path.StartsWith(Root + TEXT("/")));
			TestTrue(TEXT("subfolder is not the import root itself"), Path.Len() > Root.Len() + 1);
		}
	}

	// --- Object paths -----------------------------------------------------------------------
	{
		TestEqual(TEXT("object path inside a package directory"),
			ObjectPathIn(TEXT("/Game/MantlePlace/a1b2c3d4/Imagery"), TEXT("MI_Drape_a1b2c3d4")),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/Imagery/MI_Drape_a1b2c3d4.MI_Drape_a1b2c3d4")));

		TestEqual(TEXT("object path of an already-full package name"),
			ObjectPathOf(TEXT("/Game/MantlePlace/a1b2c3d4/Landcover/LI_water"), TEXT("LI_water")),
			FString(TEXT("/Game/MantlePlace/a1b2c3d4/Landcover/LI_water.LI_water")));

		// The two forms agree: appending the asset to the directory and then qualifying is the same
		// as qualifying a package name that already ends in the asset.
		TestEqual(TEXT("the two object-path forms agree"),
			ObjectPathIn(TEXT("/Game/X"), TEXT("T_Y")),
			ObjectPathOf(TEXT("/Game/X/T_Y"), TEXT("T_Y")));
	}

	// --- Asset names ------------------------------------------------------------------------
	{
		TestEqual(TEXT("texture from a source file"),
			TextureName(TEXT("C:/tmp/import/Imagery/Imagery.png")), FString(TEXT("T_Imagery")));
		TestEqual(TEXT("static mesh from a source file"),
			StaticMeshName(TEXT("C:/tmp/import/Mesh/Terrain.glb")), FString(TEXT("SM_Terrain")));
		TestEqual(TEXT("buildings mesh from a source file"),
			StaticMeshName(TEXT("C:/tmp/import/Buildings/Buildings.glb")), FString(TEXT("SM_Buildings")));

		// Idempotent: a source already named to the standard must not become T_T_Drape. This is not
		// hypothetical tidiness — a re-import of an asset the importer itself named would double the
		// prefix on every pass and walk the name away from the one the level references.
		TestEqual(TEXT("an already-prefixed texture is not prefixed twice"),
			TextureName(TEXT("/tmp/T_Imagery.png")), FString(TEXT("T_Imagery")));
		TestEqual(TEXT("an already-prefixed mesh is not prefixed twice"),
			StaticMeshName(TEXT("/tmp/SM_Terrain.glb")), FString(TEXT("SM_Terrain")));

		// A forward-slash path and a backslash path name the same asset. Bundles are produced on
		// Linux and imported on Windows, so both reach this function.
		TestEqual(TEXT("backslashes resolve the same as forward slashes"),
			TextureName(TEXT("C:\\tmp\\Imagery\\Imagery.png")), TextureName(TEXT("C:/tmp/Imagery/Imagery.png")));

		// The identity does NOT appear in a leaf asset name — the folder already carries it, and
		// repeating it made `MI_Drape_a1b2c3d4` unreadable in a material picker.
		TestEqual(TEXT("drape material instance"),
			DrapeMaterialName(), FString(TEXT("MI_Drape")));
		TestEqual(TEXT("tree points table"),
			TreePointsTableName(), FString(TEXT("DT_TreePoints")));
		TestEqual(TEXT("tree points row"), TreePointsRowName(0), FString(TEXT("Tree_0")));
		TestEqual(TEXT("tree points row, later"), TreePointsRowName(1234), FString(TEXT("Tree_1234")));
	}

	// --- Paint layers -----------------------------------------------------------------------
	{
		// The ASSET is prefixed. The layer's own name, which a landscape material binds to, is NOT
		// (HPS-33) — that name never passes through this module at all, which is the point. If a
		// future patch adds a LayerName() helper that prettifies anything, these assertions are the
		// ones that should stop it.
		TestEqual(TEXT("layer info asset is prefixed"),
			LayerInfoName(TEXT("water")), FString(TEXT("LI_water")));
		TestEqual(TEXT("layer info preserves the platform's case"),
			LayerInfoName(TEXT("built_up")), FString(TEXT("LI_built_up")));

		// Every material the current corpus ships, asserted as delivered: lowercase, snake_case,
		// unaltered. A material the platform adds later needs no change here.
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

	// --- Actor labels -----------------------------------------------------------------------
	{
		TestEqual(TEXT("landscape label"),
			ActorLabel(EActorKind::Landscape, Identity), FString(TEXT("MP_Landscape_a1b2c3d4")));
		TestEqual(TEXT("mesh label"),
			ActorLabel(EActorKind::Mesh, Identity), FString(TEXT("MP_Mesh_a1b2c3d4")));
		TestEqual(TEXT("buildings label"),
			ActorLabel(EActorKind::Buildings, Identity), FString(TEXT("MP_Buildings_a1b2c3d4")));

		TestEqual(TEXT("road spline prefix"),
			RoadSplineLabelPrefix(Identity), FString(TEXT("MP_RoadSpline_a1b2c3d4_")));
		TestEqual(TEXT("road spline label"),
			RoadSplineLabel(Identity, 0), FString(TEXT("MP_RoadSpline_a1b2c3d4_000")));
		TestEqual(TEXT("road spline label, zero padded"),
			RoadSplineLabel(Identity, 7), FString(TEXT("MP_RoadSpline_a1b2c3d4_007")));
		TestEqual(TEXT("road spline label past the padding width"),
			RoadSplineLabel(Identity, 1234), FString(TEXT("MP_RoadSpline_a1b2c3d4_1234")));

		// Re-import finds prior actors by matching these, so the prefix has to be a real prefix of
		// every numbered label. A mismatch here means a re-import stops cleaning up road splines and
		// starts stacking duplicates, silently.
		const FString Prefix = RoadSplineLabelPrefix(Identity);
		for (const int32 Index : { 0, 1, 9, 10, 99, 100, 999, 1000 })
		{
			TestTrue(TEXT("every road spline label starts with the prefix"),
				RoadSplineLabel(Identity, Index).StartsWith(Prefix, ESearchCase::CaseSensitive));
		}

		// The three single-actor labels are distinct from each other and none is a prefix of
		// another — the stale-actor sweep compares two of them by equality and one by StartsWith,
		// so an accidental prefix relationship would make it delete the wrong actor.
		const FString Labels[] = { ActorLabel(EActorKind::Landscape, Identity),
		                           ActorLabel(EActorKind::Mesh, Identity),
		                           ActorLabel(EActorKind::Buildings, Identity) };
		for (int32 I = 0; I < 3; ++I)
		{
			for (int32 J = I + 1; J < 3; ++J)
			{
				TestNotEqual(TEXT("actor labels are distinct"), Labels[I], Labels[J]);
				TestFalse(TEXT("no actor label prefixes another"), Labels[J].StartsWith(Labels[I]));
				TestFalse(TEXT("no actor label prefixes another, reversed"), Labels[I].StartsWith(Labels[J]));
			}
			TestFalse(TEXT("a road spline label is not one of the single-actor labels"),
				Labels[I].StartsWith(Prefix));
		}

		// Two different identities never collide, which is what makes the folder-per-order scheme
		// mean anything.
		TestNotEqual(TEXT("different identities give different labels"),
			ActorLabel(EActorKind::Landscape, TEXT("a1b2c3d4")),
			ActorLabel(EActorKind::Landscape, TEXT("99887766")));

		// ...but two identities sharing their first eight characters DO collide, and that is why
		// the caller owes a check before it deletes anything. Asserted so the hazard is recorded
		// here rather than discovered in a user's project.
		TestEqual(TEXT("identities sharing a short form share a label — the caller must guard"),
			ActorLabel(EActorKind::Landscape, TEXT("a1b2c3d4-one")),
			ActorLabel(EActorKind::Landscape, TEXT("a1b2c3d4-two")));
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
