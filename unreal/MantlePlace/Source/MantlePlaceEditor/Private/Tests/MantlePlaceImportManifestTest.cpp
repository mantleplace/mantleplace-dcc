// Copyright Mantle Place. All Rights Reserved.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "MantlePlaceImportManifest.h"
#include "MantlePlaceLandscapeWeightsLogic.h" // the band legend the corpus states the answer for
#include "MantlePlaceVaultTypes.h" // MantlePlaceMinSupportedManifestVersion
#include "Tests/MantlePlaceConformanceCorpus.h"

// Every manifest vector this suite asserts lives in the shared conformance corpus at
// tools/manifest-conformance/corpus/manifest/ (HPS-40). They used to be inline C++ literals here,
// which made them a perfectly good test suite and a completely unusable specification: a second
// host could read them only by reading C++, and would inevitably re-derive them slightly
// differently. What stays inline below is the handful of assertions about this host's own small
// helpers (ParseEpsg, DeriveCesiumTerrainPrefix) — host API surface, not cross-host contract.

namespace
{
using namespace MantlePlaceConformanceCorpus;

/** Expectation keys this host knows how to assert. The list no longer proves coverage by itself —
 *  the per-case AssertedKeys tracking does (HPS-46) — it is kept to tell "unknown key" apart from
 *  "known key declared with a type this host could not read" in the failure message. */
const TCHAR* const ConsumedExpectationKeys[] = {
	TEXT("jobId"),
	TEXT("orderId"),
	TEXT("deliveryModel"),
	TEXT("epsg"),
	TEXT("planetShape"),
	TEXT("hasHeightmap"),
	TEXT("hasDrape"),
	TEXT("hasMesh"),
	TEXT("hasBuildings"),
	TEXT("hasRoadSplines"),
	TEXT("meshAbsentReason"),
	TEXT("integrityCheckedMesh"),
	TEXT("heightmapPath"),
	TEXT("heightmapSha256"),
	TEXT("resolution"),
	TEXT("sectionSizeQuads"),
	TEXT("sectionsPerComponent"),
	TEXT("componentCountX"),
	TEXT("componentCountY"),
	TEXT("row0IsNorth"),
	TEXT("drapePath"),
	TEXT("drapeSha256"),
	TEXT("meshPath"),
	TEXT("meshUpAxis"),
	TEXT("meshSha256"),
	TEXT("buildingsPath"),
	TEXT("buildingsUpAxis"),
	TEXT("buildingsSha256"),
	TEXT("roadSplinesPath"),
	TEXT("roadSplinesSha256"),
	TEXT("hasFoliagePoints"),
	TEXT("foliagePointsPath"),
	TEXT("hasLandscapeLayers"),
	TEXT("landscapeLayerNames"),
	TEXT("landscapeLayers"),
	TEXT("materialWeightBands"),
	TEXT("cesiumTerrainPath"),
	TEXT("cesiumTerrainPrefix"),
	TEXT("quadsPerComponent"),
	TEXT("landscapeScale"),
	TEXT("landscapeScaleTolerance"),
	TEXT("landscapeSpawnLocation"),
	TEXT("landscapeSpawnToleranceCm"),
	TEXT("meshLocation"),
	TEXT("meshLocationToleranceCm"),
	TEXT("drapeWorldRectMin"),
	TEXT("drapeWorldRectMinToleranceCm"),
	TEXT("drapeWorldRectSize"),
	TEXT("drapeWorldRectSizeToleranceCm"),
};

void AssertStringExpectation(
	FAutomationTestBase& T, const FCase& Case, const TCHAR* Key, const FString& Actual)
{
	FString Expected;
	if (WantsString(Case, Key, Expected))
	{
		T.TestEqual(Case.What(Key), Actual, Expected);
	}
}

void AssertBoolExpectation(FAutomationTestBase& T, const FCase& Case, const TCHAR* Key, bool bActual)
{
	bool bExpected = false;
	if (WantsBool(Case, Key, bExpected))
	{
		T.TestTrue(
			FString::Printf(TEXT("[%s] %s is %s"), *Case.Id, Key, bExpected ? TEXT("true") : TEXT("false")),
			bActual == bExpected);
	}
}

void AssertIntExpectation(FAutomationTestBase& T, const FCase& Case, const TCHAR* Key, int32 Actual)
{
	int32 Expected = 0;
	if (WantsInt(Case, Key, Expected))
	{
		T.TestEqual(Case.What(Key), Actual, Expected);
	}
}

/** A numeric tuple with the tolerance the case states (never a tighter one we invented). A tuple
 *  declared with the wrong shape (wrong type, wrong arity, a non-number element) reads nothing and
 *  records nothing, so the UnassertedExpectations guard flags it (HPS-46) — no local check needed. */
void AssertTupleExpectation(
	FAutomationTestBase& T,
	const FCase& Case,
	const TCHAR* Key,
	const TCHAR* ToleranceKey,
	TConstArrayView<double> Actual)
{
	TArray<double> Expected;
	if (!WantsNumbers(Case, Key, Actual.Num(), Expected))
	{
		return;
	}
	const double Tolerance = ToleranceOr(Case, ToleranceKey, 1e-6);
	for (int32 Index = 0; Index < Expected.Num(); ++Index)
	{
		T.TestEqual(
			FString::Printf(TEXT("[%s] %s[%d]"), *Case.Id, Key, Index),
			Actual[Index],
			Expected[Index],
			Tolerance);
	}
}

/** A string array the case may declare, compared element-wise so a length mismatch reads clearly. */
void AssertStringArrayExpectation(
	FAutomationTestBase& T, const FCase& Case, const TCHAR* Key, const TArray<FString>& Actual)
{
	TArray<FString> Expected;
	if (!WantsStringArray(Case, Key, Expected))
	{
		return;
	}
	T.TestEqual(Case.What(Key), Actual.Num(), Expected.Num());
	for (int32 Index = 0; Index < FMath::Min(Actual.Num(), Expected.Num()); ++Index)
	{
		T.TestEqual(FString::Printf(TEXT("[%s] %s[%d]"), *Case.Id, Key, Index), Actual[Index], Expected[Index]);
	}
}

/** One `ue_ready[].value_mapping`. The corpus declares only the keys this raster's `encoding`
 *  uses, so each read is attempted and the ones the case did not declare simply record nothing —
 *  which is how one assertion path covers all five encodings without branching on the encoding. */
void AssertValueMapping(
	FAutomationTestBase& T,
	const FCase& Case,
	const FString& Path,
	const TSharedPtr<FJsonObject>& Mapping,
	const FMantlePlaceRasterValueMapping& Actual)
{
	double Number = 0.0;
	bool bFlag = false;
	FString Text;

	if (ExpectRowNumber(Case, Path, Mapping, TEXT("min"), Number))
	{
		T.TestEqual(Path + TEXT(".min"), Actual.MinValue, Number, 1e-9);
	}
	if (ExpectRowNumber(Case, Path, Mapping, TEXT("max"), Number))
	{
		T.TestEqual(Path + TEXT(".max"), Actual.MaxValue, Number, 1e-9);
	}
	if (ExpectRowNumber(Case, Path, Mapping, TEXT("nodataValue"), Number))
	{
		T.TestTrue(Path + TEXT(".nodataValue is recognised as declared"), Actual.bHasNodata);
		T.TestEqual(Path + TEXT(".nodataValue"), Actual.NodataValue, Number, 1e-9);
	}
	if (ExpectRowNumber(Case, Path, Mapping, TEXT("trueValue"), Number))
	{
		T.TestEqual(Path + TEXT(".trueValue"), Actual.TrueValue, Number, 1e-9);
	}
	if (ExpectRowNumber(Case, Path, Mapping, TEXT("falseValue"), Number))
	{
		T.TestEqual(Path + TEXT(".falseValue"), Actual.FalseValue, Number, 1e-9);
	}
	if (ExpectRowString(Case, Path, Mapping, TEXT("toValue"), Text))
	{
		T.TestEqual(Path + TEXT(".toValue"), Actual.ToValueFormula, Text);
	}
	if (ExpectRowString(Case, Path, Mapping, TEXT("units"), Text))
	{
		T.TestEqual(Path + TEXT(".units"), Actual.Units, Text);
	}
	if (ExpectRowBool(Case, Path, Mapping, TEXT("identity"), bFlag))
	{
		T.TestTrue(Path + TEXT(".identity"), Actual.bIdentity == bFlag);
	}

	const TArray<TSharedPtr<FJsonValue>>* Bands = nullptr;
	if (ExpectRowArray(Case, Path, Mapping, TEXT("bands"), Bands) && Bands != nullptr)
	{
		T.TestEqual(Path + TEXT(".bands count"), Actual.Bands.Num(), Bands->Num());
		for (int32 Index = 0; Index < Bands->Num(); ++Index)
		{
			const FString ElementPath = FString::Printf(TEXT("%s.bands[%d]"), *Path, Index);
			if (ExpectElementString(Case, ElementPath, (*Bands)[Index], Text))
			{
				T.TestEqual(ElementPath, Actual.Bands.IsValidIndex(Index) ? Actual.Bands[Index] : FString(), Text);
			}
		}
	}

	const TArray<TSharedPtr<FJsonValue>>* Classes = nullptr;
	if (ExpectRowArray(Case, Path, Mapping, TEXT("classes"), Classes) && Classes != nullptr)
	{
		T.TestEqual(Path + TEXT(".classes count"), Actual.Classes.Num(), Classes->Num());
		for (int32 Index = 0; Index < Classes->Num(); ++Index)
		{
			const FString ElementPath = FString::Printf(TEXT("%s.classes[%d]"), *Path, Index);
			if (ExpectElementNumber(Case, ElementPath, (*Classes)[Index], Number))
			{
				T.TestEqual(ElementPath, Actual.Classes.IsValidIndex(Index) ? Actual.Classes[Index] : 0,
					static_cast<int32>(Number));
			}
		}
	}
}

/** `landscapeLayers` — the whole `unreal.landscape_layers` block, layer by layer and companion by
 *  companion. Every leaf the case declares is read here, which is the point: the block's defect was
 *  that it parsed to nothing at all. */
void AssertLandscapeLayers(FAutomationTestBase& T, const FCase& Case, const FMantlePlaceVaultManifest& M)
{
	TArray<TSharedPtr<FJsonObject>> Rows;
	if (!WantsObjectRows(Case, TEXT("landscapeLayers"), Rows))
	{
		return;
	}
	T.TestEqual(Case.What(TEXT("landscape layer count")), M.LandscapeLayers.Num(), Rows.Num());

	for (int32 Index = 0; Index < Rows.Num(); ++Index)
	{
		const FString RowPath = FString::Printf(TEXT("landscapeLayers[%d]"), Index);
		const TSharedPtr<FJsonObject>& Row = Rows[Index];
		FString Text;

		// Order is the parser's promise (sorted by name), so the row index IS the layer index.
		const FMantlePlaceLandscapeLayer* Layer =
			M.LandscapeLayers.IsValidIndex(Index) ? &M.LandscapeLayers[Index] : nullptr;

		if (ExpectRowString(Case, RowPath, Row, TEXT("name"), Text))
		{
			T.TestEqual(RowPath + TEXT(".name"), Layer != nullptr ? Layer->Name : FString(), Text);
			// And the same layer must be reachable by name, not only by position.
			T.TestTrue(RowPath + TEXT(" is findable by name"), M.FindLandscapeLayer(*Text) == Layer);
		}
		if (ExpectRowString(Case, RowPath, Row, TEXT("path"), Text))
		{
			T.TestEqual(RowPath + TEXT(".path"), Layer != nullptr ? Layer->Path : FString(), Text);
		}
		if (ExpectRowString(Case, RowPath, Row, TEXT("sha256"), Text))
		{
			T.TestEqual(RowPath + TEXT(".sha256"), Layer != nullptr ? Layer->Sha256 : FString(), Text);
		}

		const TArray<TSharedPtr<FJsonValue>>* Materials = nullptr;
		if (ExpectRowArray(Case, RowPath, Row, TEXT("materials"), Materials) && Materials != nullptr)
		{
			T.TestEqual(RowPath + TEXT(".materials count"),
				Layer != nullptr ? Layer->Materials.Num() : 0, Materials->Num());
			for (int32 Band = 0; Band < Materials->Num(); ++Band)
			{
				const FString ElementPath = FString::Printf(TEXT("%s.materials[%d]"), *RowPath, Band);
				if (ExpectElementString(Case, ElementPath, (*Materials)[Band], Text))
				{
					T.TestEqual(ElementPath,
						(Layer != nullptr && Layer->Materials.IsValidIndex(Band)) ? Layer->Materials[Band] : FString(),
						Text);
				}
			}
		}

		const TArray<TSharedPtr<FJsonValue>>* UeReady = nullptr;
		if (!ExpectRowArray(Case, RowPath, Row, TEXT("ueReady"), UeReady) || UeReady == nullptr)
		{
			continue;
		}
		T.TestEqual(RowPath + TEXT(".ueReady count"),
			Layer != nullptr ? Layer->UeReady.Num() : 0, UeReady->Num());

		for (int32 Companion = 0; Companion < UeReady->Num(); ++Companion)
		{
			const FString ElementPath = FString::Printf(TEXT("%s.ueReady[%d]"), *RowPath, Companion);
			const TSharedPtr<FJsonObject> Element = ExpectElementObject((*UeReady)[Companion]);
			if (!Element.IsValid())
			{
				continue;
			}
			const FMantlePlaceUeReadyRaster* Raster =
				(Layer != nullptr && Layer->UeReady.IsValidIndex(Companion)) ? &Layer->UeReady[Companion] : nullptr;

			double Number = 0.0;
			if (ExpectRowString(Case, ElementPath, Element, TEXT("path"), Text))
			{
				T.TestEqual(ElementPath + TEXT(".path"), Raster != nullptr ? Raster->Path : FString(), Text);
			}
			if (ExpectRowString(Case, ElementPath, Element, TEXT("sha256"), Text))
			{
				T.TestEqual(ElementPath + TEXT(".sha256"), Raster != nullptr ? Raster->Sha256 : FString(), Text);
			}
			if (ExpectRowString(Case, ElementPath, Element, TEXT("encoding"), Text))
			{
				T.TestEqual(ElementPath + TEXT(".encoding"), Raster != nullptr ? Raster->Encoding : FString(), Text);
			}
			if (ExpectRowNumber(Case, ElementPath, Element, TEXT("width"), Number))
			{
				T.TestEqual(ElementPath + TEXT(".width"), Raster != nullptr ? Raster->Width : 0,
					static_cast<int32>(Number));
			}
			if (ExpectRowNumber(Case, ElementPath, Element, TEXT("height"), Number))
			{
				T.TestEqual(ElementPath + TEXT(".height"), Raster != nullptr ? Raster->Height : 0,
					static_cast<int32>(Number));
			}
			if (ExpectRowNumber(Case, ElementPath, Element, TEXT("sizeBytes"), Number))
			{
				T.TestEqual(ElementPath + TEXT(".sizeBytes"), Raster != nullptr ? Raster->SizeBytes : 0,
					static_cast<int64>(Number));
			}

			TSharedPtr<FJsonObject> Mapping;
			if (ExpectRowObject(Case, ElementPath, Element, TEXT("mapping"), Mapping))
			{
				static const FMantlePlaceRasterValueMapping Empty;
				AssertValueMapping(T, Case, ElementPath + TEXT(".mapping"), Mapping,
					Raster != nullptr ? Raster->ValueMapping : Empty);
			}
		}
	}
}

/** `materialWeightBands` — the band legend resolved to (companion, channel). This is the one
 *  derivation the legend exists for, and it is cross-host: any host with a weight-blended material
 *  resolves it identically, so the answer lives in the corpus rather than in this suite. */
void AssertMaterialWeightBands(
	FAutomationTestBase& T, const FCase& Case, const FMantlePlaceVaultManifest& M)
{
	TArray<TSharedPtr<FJsonObject>> Rows;
	if (!WantsObjectRows(Case, TEXT("materialWeightBands"), Rows))
	{
		return;
	}

	TArray<FMantlePlaceWeightBand> Bands;
	FString Error;
	const FMantlePlaceLandscapeLayer* Layer = M.FindLandscapeLayer(TEXT("material_weights"));
	if (Layer == nullptr)
	{
		T.AddError(Case.What(TEXT("no material_weights layer to resolve bands from")));
		return;
	}
	T.TestTrue(Case.What(TEXT("the band legend resolves")),
		FMantlePlaceLandscapeWeightsLogic::ResolveBands(*Layer, Bands, Error));
	T.TestEqual(Case.What(TEXT("one band per material")), Bands.Num(), Rows.Num());

	for (int32 Index = 0; Index < Rows.Num(); ++Index)
	{
		const FString RowPath = FString::Printf(TEXT("materialWeightBands[%d]"), Index);
		const FMantlePlaceWeightBand* Band = Bands.IsValidIndex(Index) ? &Bands[Index] : nullptr;
		FString Text;
		double Number = 0.0;

		if (ExpectRowString(Case, RowPath, Rows[Index], TEXT("material"), Text))
		{
			T.TestEqual(RowPath + TEXT(".material"), Band != nullptr ? Band->Material : FString(), Text);
		}
		if (ExpectRowString(Case, RowPath, Rows[Index], TEXT("image"), Text))
		{
			T.TestEqual(RowPath + TEXT(".image"), Band != nullptr ? Band->ImagePath : FString(), Text);
		}
		if (ExpectRowNumber(Case, RowPath, Rows[Index], TEXT("channel"), Number))
		{
			T.TestEqual(RowPath + TEXT(".channel"), Band != nullptr ? Band->Channel : -1,
				static_cast<int32>(Number));
		}
	}
}

/** Apply every expectation the case declares to a parsed manifest. */
void AssertExpectations(FAutomationTestBase& T, const FCase& Case, const FMantlePlaceVaultManifest& M)
{
	AssertStringExpectation(T, Case, TEXT("jobId"), M.JobId);
	AssertStringExpectation(T, Case, TEXT("orderId"), M.OrderId);
	AssertStringExpectation(T, Case, TEXT("deliveryModel"), M.DeliveryModel);
	AssertStringExpectation(T, Case, TEXT("planetShape"), M.PlanetShape);
	AssertStringExpectation(T, Case, TEXT("meshAbsentReason"), M.MeshAbsentReason);
	AssertStringExpectation(T, Case, TEXT("heightmapPath"), M.HeightmapPath);
	AssertStringExpectation(T, Case, TEXT("heightmapSha256"), M.HeightmapSha256);
	AssertStringExpectation(T, Case, TEXT("drapePath"), M.DrapePath);
	AssertStringExpectation(T, Case, TEXT("drapeSha256"), M.DrapeSha256);
	AssertStringExpectation(T, Case, TEXT("meshPath"), M.MeshPath);
	AssertStringExpectation(T, Case, TEXT("meshUpAxis"), M.MeshUpAxis);
	AssertStringExpectation(T, Case, TEXT("meshSha256"), M.MeshSha256);
	AssertStringExpectation(T, Case, TEXT("buildingsPath"), M.BuildingsPath);
	AssertStringExpectation(T, Case, TEXT("buildingsUpAxis"), M.BuildingsUpAxis);
	AssertStringExpectation(T, Case, TEXT("buildingsSha256"), M.BuildingsSha256);
	AssertStringExpectation(T, Case, TEXT("roadSplinesPath"), M.RoadSplinesPath);
	AssertStringExpectation(T, Case, TEXT("roadSplinesSha256"), M.RoadSplinesSha256);
	AssertStringExpectation(T, Case, TEXT("foliagePointsPath"), M.FoliagePointsPath);
	AssertStringExpectation(T, Case, TEXT("cesiumTerrainPath"), M.CesiumTerrainPath);
	AssertStringExpectation(
		T, Case, TEXT("cesiumTerrainPrefix"),
		MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(M.CesiumTerrainPath));

	AssertBoolExpectation(T, Case, TEXT("hasHeightmap"), M.bHasHeightmap);
	AssertBoolExpectation(T, Case, TEXT("hasDrape"), M.bHasDrape);
	AssertBoolExpectation(T, Case, TEXT("hasMesh"), M.bHasMesh);
	AssertBoolExpectation(T, Case, TEXT("hasBuildings"), M.bHasBuildings);
	AssertBoolExpectation(T, Case, TEXT("hasRoadSplines"), M.bHasRoadSplines);
	AssertBoolExpectation(T, Case, TEXT("hasFoliagePoints"), M.bHasFoliagePoints);
	AssertBoolExpectation(T, Case, TEXT("hasLandscapeLayers"), M.bHasLandscapeLayers);
	AssertBoolExpectation(T, Case, TEXT("row0IsNorth"), M.bRow0IsNorth);
	// An empty mesh sha means the manifest declared none, so the integrity check is skipped —
	// "valid but unverified", never "verified".
	AssertBoolExpectation(T, Case, TEXT("integrityCheckedMesh"), !M.MeshSha256.IsEmpty());

	AssertIntExpectation(T, Case, TEXT("epsg"), M.Epsg);
	AssertIntExpectation(T, Case, TEXT("quadsPerComponent"), M.GetQuadsPerComponent());
	AssertIntExpectation(T, Case, TEXT("resolution"), M.Resolution);
	AssertIntExpectation(T, Case, TEXT("sectionSizeQuads"), M.SectionSizeQuads);
	AssertIntExpectation(T, Case, TEXT("sectionsPerComponent"), M.SectionsPerComponent);
	AssertIntExpectation(T, Case, TEXT("componentCountX"), M.ComponentCountX);
	AssertIntExpectation(T, Case, TEXT("componentCountY"), M.ComponentCountY);

	const FVector Scale = M.GetLandscapeScale();
	AssertTupleExpectation(T, Case, TEXT("landscapeScale"), TEXT("landscapeScaleTolerance"),
		{ Scale.X, Scale.Y, Scale.Z });

	const FVector Spawn = M.GetLandscapeSpawnLocation();
	AssertTupleExpectation(T, Case, TEXT("landscapeSpawnLocation"), TEXT("landscapeSpawnToleranceCm"),
		{ Spawn.X, Spawn.Y, Spawn.Z });

	const FVector MeshLocation = M.GetMeshLocation();
	AssertTupleExpectation(T, Case, TEXT("meshLocation"), TEXT("meshLocationToleranceCm"),
		{ MeshLocation.X, MeshLocation.Y, MeshLocation.Z });

	FVector2D DrapeMin, DrapeSize;
	M.GetDrapeWorldRect(DrapeMin, DrapeSize);
	AssertTupleExpectation(T, Case, TEXT("drapeWorldRectMin"), TEXT("drapeWorldRectMinToleranceCm"),
		{ DrapeMin.X, DrapeMin.Y });
	AssertTupleExpectation(T, Case, TEXT("drapeWorldRectSize"), TEXT("drapeWorldRectSizeToleranceCm"),
		{ DrapeSize.X, DrapeSize.Y });

	TArray<FString> LayerNames;
	for (const FMantlePlaceLandscapeLayer& Layer : M.LandscapeLayers)
	{
		LayerNames.Add(Layer.Name);
	}
	AssertStringArrayExpectation(T, Case, TEXT("landscapeLayerNames"), LayerNames);
	AssertLandscapeLayers(T, Case, M);
	AssertMaterialWeightBands(T, Case, M);
}
} // namespace

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceImportManifestTest,
	"MantlePlace.Import.Manifest",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceImportManifestTest::RunTest(const FString& Parameters)
{
	// --- The corpus drives every manifest vector -------------------------------------------
	TArray<FCase> Cases;
	FString LoadError;
	if (!LoadGroup(TEXT("manifest"), Cases, LoadError))
	{
		AddError(FString::Printf(TEXT("conformance corpus unusable: %s"), *LoadError));
		return false;
	}

	// The corpus is pinned at one manifest version; this parser has its own floor. Equality was
	// the rule while every host repinned together. Hosts repin INDEPENDENTLY now — the
	// corpus tracks the newest published contract, while a host that has not yet taken the clean
	// break still floors lower and accepts the newer shape (the readers gate `<`, not `!=`). What
	// must never happen is the corpus falling BELOW this floor: then every accept case below is a
	// document this parser refuses, and a dozen derived assertions fail with confusing messages
	// instead of this one saying it once, loudly.
	TestTrue(
		FString::Printf(
			TEXT("corpus manifestVersion (%s) >= this host's MinSupportedManifestVersion (%s) "
			     "(clean break, HPS-31; independent repin)"),
			*PinnedManifestVersion(),
			*MantlePlaceMinSupportedManifestVersion),
		!MantlePlaceIsManifestVersionBelowFloor(PinnedManifestVersion(), MantlePlaceMinSupportedManifestVersion));

	TSet<FString> Driven;
	for (const FCase& Case : Cases)
	{
		// Known-answer tables are not documents to feed the parser; they are dispatched by id
		// below and recorded into Driven there. Their `expectations` are swept after that
		// dispatch, not here — sweeping now would read every vector case's keys as unasserted
		// because the assertions that record them have not run yet.
		if (Case.IsVector())
		{
			continue;
		}
		Driven.Add(Case.Id);

		FString Error;
		const FMantlePlaceVaultManifest M = MantlePlaceImportManifest::Parse(Case.Payload, Error);

		if (Case.IsAccept())
		{
			TestTrue(Case.What(TEXT("accepted")), M.bValid);
			if (!M.bValid)
			{
				AddError(FString::Printf(TEXT("[%s] rejected with: %s"), *Case.Id, *Error));
			}
			// An accepted manifest must come back with a CLEAN error string. A parser that fills
			// OutError and still reports bValid leaves the importer showing a warning it cannot act on.
			TestEqual(Case.What(TEXT("accepted with no error text")), Error, FString());
		}
		else if (Case.IsReject())
		{
			TestFalse(Case.What(TEXT("rejected")), M.bValid);
			TestFalse(Case.What(TEXT("rejection states a reason")), Error.IsEmpty());
		}
		else
		{
			AddError(FString::Printf(TEXT("[%s] unsupported expect '%s' in the manifest group"),
				*Case.Id, *Case.Expect));
			continue;
		}

		if (!Case.ErrorContains.IsEmpty())
		{
			TestTrue(
				FString::Printf(TEXT("[%s] message contains \"%s\" (got: %s)"),
					*Case.Id, *Case.ErrorContains, *Error),
				Error.Contains(Case.ErrorContains));
		}

		// A reject case may still carry expectations — values the parser must have read BEFORE
		// refusing (HPS-37), e.g. the orderId that lets a base-on-demand bundle be materialized.
		AssertExpectations(*this, Case, M);

		// Consumption is proven by what was ASSERTED, not by the allow-list alone: a declared key
		// nothing read — unknown, mistyped, or on an assertion path that never ran — fails (HPS-46).
		for (const FString& Problem : UnassertedExpectations(Case, ConsumedExpectationKeys))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}

		// And below the top level: recording `items` proves the array was reached, never
		// that anything inside it was read (HPS-46b).
		for (const FString& Problem : UnassertedNestedExpectations(Case))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}
	}

	// --- HPS-47: the materialization predicate, driven by the neutral-signal vectors ---------
	// Each row embeds a complete v18 manifest; the revit-block-only and dcc-readiness-only rows are
	// the ones that catch a host keying off its own block — materialized bundles carrying nothing
	// this host imports, which must still read as materialized.
	if (const FCase* Case = FindCase(Cases, TEXT("manifest.materializationSignals")))
	{
		Driven.Add(Case->Id);
		const TArray<TSharedPtr<FJsonObject>> Vectors = Rows(*Case, TEXT("vectors"));
		TestTrue(Case->What(TEXT("has vectors")), Vectors.Num() > 0);
		for (const TSharedPtr<FJsonObject>& Row : Vectors)
		{
			const FString Name = RowString(Row, TEXT("name"));
			const TSharedPtr<FJsonObject>* Manifest = nullptr;
			if (!Row.IsValid() || !Row->TryGetObjectField(TEXT("manifest"), Manifest) || Manifest == nullptr)
			{
				AddError(FString::Printf(TEXT("[%s] row \"%s\" has no embedded manifest"), *Case->Id, *Name));
				continue;
			}
			const bool bExpected = RowBool(Row, TEXT("materialized"));
			TestTrue(
				FString::Printf(TEXT("[%s] \"%s\" -> materialized == %s"),
					*Case->Id, *Name, bExpected ? TEXT("true") : TEXT("false")),
				MantlePlaceImportManifest::IsBundleMaterialized(*Manifest) == bExpected);
		}
	}
	else
	{
		AddError(TEXT("corpus case manifest.materializationSignals has gone missing"));
	}

	// Vector cases' `expectations`, swept once every id-dispatched assertion above has run. HPS-46
	// exempts a case skipped wholesale by appliesTo, never one this host executes — and a vector
	// case is executed, just through a different door than a document case.
	for (const FCase& Case : Cases)
	{
		if (!Case.IsVector())
		{
			continue;
		}
		for (const FString& Problem : UnassertedExpectations(Case, ConsumedExpectationKeys))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}

		// And below the top level: recording `items` proves the array was reached, never
		// that anything inside it was read (HPS-46b).
		for (const FString& Problem : UnassertedNestedExpectations(Case))
		{
			AddError(FString::Printf(TEXT("[%s] %s"), *Case.Id, *Problem));
		}
	}

	for (const FString& Missing : UndrivenCases(Cases, Driven))
	{
		AddError(FString::Printf(
			TEXT("corpus case '%s' is in the manifest group but nothing in this suite drives it. ")
			TEXT("A host consumes every case in the groups it claims (HPS-41)."),
			*Missing));
	}

	// --- Host-local helpers the corpus does not (and should not) specify --------------------
	// Zip-entry prefix derivation and EPSG string parsing are this plugin's own API, not part of
	// the cross-host contract: another host reads `layout.cesiumTerrain` verbatim and has no
	// equivalent function to conform to (DOC-06).
	{
		TestEqual(TEXT("empty path yields empty prefix"),
			MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(FString()), FString());
		TestEqual(TEXT("bare filename (no dir) yields empty prefix, not \"/\""),
			MantlePlaceImportManifest::DeriveCesiumTerrainPrefix(TEXT("layer.json")), FString());

		TestEqual(TEXT("epsg from string"), MantlePlaceImportManifest::ParseEpsg(TEXT("EPSG:32613")), 32613);
		TestEqual(TEXT("epsg from bare"), MantlePlaceImportManifest::ParseEpsg(TEXT("32618")), 32618);
		TestEqual(TEXT("epsg empty"), MantlePlaceImportManifest::ParseEpsg(TEXT("")), 0);
	}

	// --- The world axis mapping ------------------------------------------------------------
	// The one conversion every projected coordinate goes through, plus the mesh path's separate
	// correction. Both are asserted by their EFFECT on a known offset rather than by restating
	// the literal, so a sign or an axis that drifts fails here rather than in a screenshot.
	{
		// 100 m north of the origin is +X; 100 m east is +Y. Both components each time, because a
		// half-done swap satisfies either one alone.
		const FVector North = FMantlePlaceVaultManifest::ProjectedToUeCm(0.0, 100.0, 0.0);
		TestEqual(TEXT("100 m north -> +10000 cm X"), North.X, 10000.0, 1e-9);
		TestEqual(TEXT("100 m north -> 0 Y"), North.Y, 0.0, 1e-9);

		const FVector East = FMantlePlaceVaultManifest::ProjectedToUeCm(100.0, 0.0, 0.0);
		TestEqual(TEXT("100 m east -> +10000 cm Y"), East.Y, 10000.0, 1e-9);
		TestEqual(TEXT("100 m east -> 0 X"), East.X, 0.0, 1e-9);

		const FVector Up = FMantlePlaceVaultManifest::ProjectedToUeCm(0.0, 0.0, 100.0);
		TestEqual(TEXT("100 m up -> +10000 cm Z"), Up.Z, 10000.0, 1e-9);

		// Interchange lands glTF with East on +X and South on +Y. The mesh rotation must carry
		// mesh-space east onto world +Y and mesh-space south onto world -X, WITHOUT mirroring:
		// a determinant of +1 is the thing that distinguishes this from the old `-1` Y workaround.
		const FRotator MeshRot = FMantlePlaceVaultManifest::GetMeshRotation();
		const FVector MeshEast = MeshRot.RotateVector(FVector(1.0, 0.0, 0.0));
		const FVector MeshSouth = MeshRot.RotateVector(FVector(0.0, 1.0, 0.0));
		TestEqual(TEXT("mesh +X (east) -> world +Y"), MeshEast.Y, 1.0, 1e-6);
		TestEqual(TEXT("mesh +X (east) -> no world X"), MeshEast.X, 0.0, 1e-6);
		TestEqual(TEXT("mesh +Y (south) -> world -X"), MeshSouth.X, -1.0, 1e-6);
		TestEqual(TEXT("mesh +Y (south) -> no world Y"), MeshSouth.Y, 0.0, 1e-6);
		TestEqual(TEXT("mesh correction is a rotation, not a mirror (det +1)"),
			FVector::CrossProduct(MeshEast, MeshSouth).Z, FVector::CrossProduct(
				FVector(1.0, 0.0, 0.0), FVector(0.0, 1.0, 0.0)).Z, 1e-6);
	}

	// --- Bundle-level behaviour with no cross-host analogue --------------------------------
	// cesium_terrain streaming is a Cesium-for-Unreal concern; the retired pre-v13 tile-count key
	// is Unreal's own deleted fallback. Neither belongs in a corpus a Revit host must consume.
	{
		FString Error;
		const FMantlePlaceVaultManifest M = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\",\"version\":\"1.0.0\",")
		        TEXT("\"layout\":{\"cesium_terrain\":\"Elevation/Terrain/layer.json\"},")
		            TEXT("\"cesium_terrain\":{\"present\":true,\"tile_count\":1287},")
		                TEXT("\"terrain\":{\"cesiumTerrainTileCount\":42},")
		                    TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}}"),
		    Error);
		TestEqual(TEXT("tile count"), M.CesiumTerrainTileCount, 1287);
		TestTrue(TEXT("bundle has cesium terrain"), M.bHasCesiumTerrain);

		// The retired `terrain` block is doubly dead at 1.0.0: the key map deletes the block
		// outright, and this host had already deleted its fallback to the key inside it. Kept as a
		// regression guard because a retired block that reappears in a bundle must be IGNORED, not
		// resurrected as a fallback — that is how a clean break becomes dual-parsing.
		Error.Reset();
		const FMantlePlaceVaultManifest Legacy = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\",\"version\":\"1.0.0\",")
		        TEXT("\"layout\":{\"cesium_terrain\":\"Elevation/Terrain/layer.json\"},")
		            TEXT("\"terrain\":{\"cesiumTerrainTileCount\":42},")
		                TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}}"),
		    Error);
		TestEqual(TEXT("retired pre-v13 tile-count key is ignored"), Legacy.CesiumTerrainTileCount, 0);
	}

	// --- The version gate, both directions --------------------------------------------------
	// Too old and too new are DIFFERENT refusals with different remedies (re-download vs update
	// the plugin), and a bare "unsupported" message makes them indistinguishable to the user.
	{
		FString Error;
		const FMantlePlaceVaultManifest Integer = MantlePlaceImportManifest::Parse(
		    TEXT("{\"jobId\":\"x\",\"version\":19,")
		        TEXT("\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}"),
		    Error);
		TestFalse(TEXT("an integer-era manifest is refused"), Integer.bValid);
		TestTrue(TEXT("refusal names it as no longer supported"), Error.Contains(TEXT("no longer supported")));

		Error.Reset();
		const FMantlePlaceVaultManifest Absent = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\"}"), Error);
		TestFalse(TEXT("an absent version is refused"), Absent.bValid);
		TestTrue(TEXT("absent version is named, not shown as 0"), Error.Contains(TEXT("(absent)")));

		Error.Reset();
		const FMantlePlaceVaultManifest Future = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\",\"version\":\"2.0.0\",")
		        TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}}"),
		    Error);
		TestFalse(TEXT("an unknown higher MAJOR is refused"), Future.bValid);
		TestTrue(TEXT("the refusal says to update the plugin, not to re-download"),
			Error.Contains(TEXT("Update the Mantle Place plugin")));

		Error.Reset();
		const FMantlePlaceVaultManifest AdditiveMinor = MantlePlaceImportManifest::Parse(
		    TEXT("{\"job_id\":\"x\",\"version\":\"1.7.3\",\"an_unknown_future_key\":42,")
		        TEXT("\"hosts\":{\"unreal\":{\"mesh_alternative\":{\"path\":\"Mesh/Terrain.glb\"}}}}"),
		    Error);
		TestTrue(TEXT("a higher MINOR is accepted — minors are strictly additive"), AdditiveMinor.bValid);
	}

	// --- The base-on-demand guidance string (surface copy, not contract) --------------------
	// The corpus case manifest.baseOnDemand pins the *data* read off a base marker bundle; the
	// wording steered at the user is this host's UI, so it is asserted here.
	{
		const FCase* BaseCase = FindCase(Cases, TEXT("manifest.baseOnDemand"));
		if (BaseCase == nullptr)
		{
			AddError(TEXT("corpus case manifest.baseOnDemand has gone missing"));
		}
		else
		{
			FString Error;
			MantlePlaceImportManifest::Parse(BaseCase->Payload, Error);
			TestTrue(TEXT("guidance mentions the vault"), Error.Contains(TEXT("mantle.place/vault")));
			TestTrue(TEXT("guidance offers Stream into Cesium as an interim preview"),
			         Error.Contains(TEXT("Stream into Cesium")));
		}
	}

	return true;
}

// The reader self-test (HPS-46): the corpus reader itself is proven against the deliberately
// broken fixtures at tools/manifest-conformance/corpus/self-test/. Every fixture must be
// REJECTED — a fixture that passes is the failure. This proves the READER, where the corpus
// proper proves the parser; the two are never mixed in one index. Each fixture's index entry
// declares its one break in `selfTestFailure`; the classes asserted below mirror that list.
IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceConformanceReaderSelfTest,
	"MantlePlace.Conformance.ReaderSelfTest",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceConformanceReaderSelfTest::RunTest(const FString& Parameters)
{
	const FString CorpusRoot = FindCorpusRoot();
	if (CorpusRoot.IsEmpty())
	{
		AddError(TEXT("could not locate tools/manifest-conformance/corpus/ — without it the reader ")
		         TEXT("self-test (HPS-46) cannot run, and it must fail rather than skip"));
		return false;
	}
	const FString SelfTestDir = FPaths::Combine(CorpusRoot, TEXT("self-test"));

	// --- The structural fixtures: missing file, undeclared malformed bytes, duplicate id ----
	// The load must fail AND name every structural break — a reader that stops at the first one
	// would leave the other fixture classes unproven.
	TArray<FCase> Cases;
	FString Error;
	TestFalse(TEXT("a rotten index does not load cleanly"),
		LoadGroupFromDir(SelfTestDir, TEXT("manifest"), Cases, Error));
	TestTrue(TEXT("missing-file fixture is flagged (selfTestFailure: missingFile)"),
		Error.Contains(TEXT("selfTest.missingFile")) && Error.Contains(TEXT("missing vector file")));
	TestTrue(TEXT("undeclared-malformed fixture is flagged (selfTestFailure: malformedCase)"),
		Error.Contains(TEXT("selfTest.malformedCase")) && Error.Contains(TEXT("malformedJson")));
	TestTrue(TEXT("duplicate-id fixture is flagged (selfTestFailure: duplicateId)"),
		Error.Contains(TEXT("selfTest.duplicateId")) && Error.Contains(TEXT("more than once")));

	// --- The per-case fixtures, driven off the cases that DID load ---------------------------
	// A host that consumes `orderId` (and nothing else) is simulated here; the fixtures prove the
	// asserted-keys mechanics an allow-list reader gets wrong.
	static const TCHAR* const SelfTestConsumedKeys[] = { TEXT("orderId") };

	// An expectations key NO host may ever consume (`selfTest*` is reserved for exactly this) must
	// come back as "not asserted", or a platform session's new assertion silently binds nobody.
	if (const FCase* Unknown = FindCase(Cases, TEXT("selfTest.unknownExpectationKey")))
	{
		FString OrderId;
		TestTrue(Unknown->What(TEXT("orderId reads as the consumed key it is")),
			WantsString(*Unknown, TEXT("orderId"), OrderId));
		const TArray<FString> Problems = UnassertedExpectations(*Unknown, SelfTestConsumedKeys);
		TestEqual(Unknown->What(TEXT("exactly one unasserted key")), Problems.Num(), 1);
		TestTrue(Unknown->What(TEXT("the unknown key fails as \"does not assert\"")),
			Problems.Num() == 1
				&& Problems[0].Contains(TEXT("selfTestNeverConsumed"))
				&& Problems[0].Contains(TEXT("does not assert")));
	}
	else
	{
		AddError(TEXT("self-test case selfTest.unknownExpectationKey did not load"));
	}

	// A universally consumed key declared with the wrong JSON type (`orderId: 999`) must read as
	// NOTHING and then fail as unasserted — the exact asserted-keys bug: an allow-list reader
	// stays green.
	if (const FCase* WrongType = FindCase(Cases, TEXT("selfTest.wrongTypeExpectation")))
	{
		FString OrderId;
		TestFalse(WrongType->What(TEXT("a number does not read as a string")),
			WantsString(*WrongType, TEXT("orderId"), OrderId));
		const TArray<FString> Problems = UnassertedExpectations(*WrongType, SelfTestConsumedKeys);
		TestEqual(WrongType->What(TEXT("exactly one unasserted key")), Problems.Num(), 1);
		TestTrue(WrongType->What(TEXT("the mistyped key fails as \"unexpected JSON type\"")),
			Problems.Num() == 1
				&& Problems[0].Contains(TEXT("orderId"))
				&& Problems[0].Contains(TEXT("unexpected JSON type")));
	}
	else
	{
		AddError(TEXT("self-test case selfTest.wrongTypeExpectation did not load"));
	}

	// --- The nested fixture: the obligation reaches BELOW the top level (HPS-46b) ------------
	// The top level is entirely satisfied here, so a reader that stops there reports this case
	// covered — which is the whole reason the rule exists.
	if (const FCase* Nested = FindCase(Cases, TEXT("selfTest.nestedUnreadExpectation")))
	{
		FString OrderId;
		TestTrue(Nested->What(TEXT("the top-level orderId reads")),
			WantsString(*Nested, TEXT("orderId"), OrderId));

		TArray<TSharedPtr<FJsonObject>> Rows;
		TestTrue(Nested->What(TEXT("the top-level items key reads")),
			WantsObjectRows(*Nested, TEXT("items"), Rows));
		TestEqual(Nested->What(TEXT("the fixture has one row")), Rows.Num(), 1);

		if (Rows.Num() == 1)
		{
			const FString Path = TEXT("items[0]");
			FString Text;
			TestTrue(Nested->What(TEXT("items[0].orderId reads")),
				ExpectRowString(*Nested, Path, Rows[0], TEXT("orderId"), Text));

			// An explicit null is a VALUE and counts as read — otherwise `sha256: null`, the row
			// ⛔HPS-27 exists for, becomes the one leaf a suite skips for free.
			TestFalse(Nested->What(TEXT("items[0].sha256 is null, so there is no value to assert")),
				ExpectRowString(*Nested, Path, Rows[0], TEXT("sha256"), Text));

			// The coercion half (one level down): `status` is a number, and TryGetStringField
			// would hand back "404" and mark the path read. A strict read gets NOTHING.
			TestFalse(Nested->What(TEXT("a strictly typed read gets nothing from a number")),
				ExpectRowString(*Nested, Path, Rows[0], TEXT("status"), Text));
		}

		TestEqual(Nested->What(TEXT("HPS-46 alone reports this fixture covered")),
			UnassertedExpectations(*Nested, SelfTestConsumedKeys).Num(), 0);

		const TArray<FString> Unread = UnassertedNestedExpectations(*Nested);
		TestEqual(Nested->What(TEXT("both unread nested keys are flagged")), Unread.Num(), 2);
		TestTrue(Nested->What(TEXT("the key nothing asserts is named, with its path")),
			Unread.ContainsByPredicate([](const FString& Problem)
			{
				return Problem.Contains(TEXT("items[0].selfTestNeverReadNested"));
			}));
		TestTrue(Nested->What(TEXT("the wrong-typed key fails identically, not by coercion")),
			Unread.ContainsByPredicate([](const FString& Problem)
			{
				return Problem.Contains(TEXT("items[0].status"));
			}));
		TestFalse(Nested->What(TEXT("prose is exempt at depth, not only at the top level")),
			Unread.ContainsByPredicate([](const FString& Problem)
			{
				return Problem.Contains(TEXT("selfTestNote"));
			}));
		TestFalse(Nested->What(TEXT("and an explicit null that WAS read is not reported")),
			Unread.ContainsByPredicate([](const FString& Problem)
			{
				return Problem.Contains(TEXT("items[0].sha256"));
			}));
	}
	else
	{
		AddError(TEXT("self-test case selfTest.nestedUnreadExpectation did not load"));
	}

	// --- The orphan file: on disk, absent from `cases` — found by the directory sweep --------
	{
		TArray<FString> Orphans;
		FString SweepError;
		TestTrue(TEXT("the directory sweep runs"), UnindexedCaseFiles(SelfTestDir, Orphans, SweepError));
		TestTrue(TEXT("the orphan fixture is flagged (index orphanFiles: cases/orphan.json)"),
			Orphans.Contains(TEXT("cases/orphan.json")));
		TestEqual(TEXT("nothing else is flagged (broken-index-*/ are nested corpora, not orphans)"),
			Orphans.Num(), 1);
	}

	// --- The broken-index siblings: each must FAIL to load, never resolve to zero cases ------
	for (const TCHAR* Broken : { TEXT("broken-index-json"), TEXT("broken-index-schema") })
	{
		TArray<FCase> BrokenCases;
		FString BrokenError;
		TestFalse(FString::Printf(TEXT("%s fails to load"), Broken),
			LoadGroupFromDir(FPaths::Combine(SelfTestDir, Broken), TEXT("manifest"), BrokenCases, BrokenError));
		TestTrue(FString::Printf(TEXT("%s failure states a reason and yields no cases"), Broken),
			BrokenCases.Num() == 0 && !BrokenError.IsEmpty());
	}

	return true;
}

#endif // WITH_DEV_AUTOMATION_TESTS
