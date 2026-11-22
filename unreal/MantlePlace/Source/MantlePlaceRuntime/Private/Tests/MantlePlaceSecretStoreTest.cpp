// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS && PLATFORM_WINDOWS

#include "MantlePlaceSecretStore.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FMantlePlaceSecretStoreTest,
	"MantlePlace.Auth.SecretStore",
	EAutomationTestFlags_ApplicationContextMask | EAutomationTestFlags::ProductFilter)

bool FMantlePlaceSecretStoreTest::RunTest(const FString& Parameters)
{
	TUniquePtr<IMantlePlaceSecretStore> Store = IMantlePlaceSecretStore::Create();
	TestTrue(TEXT("DPAPI store is created"), Store.IsValid());
	TestTrue(TEXT("DPAPI store is persistent on Windows"), Store->IsPersistent());

	const FString Key = TEXT("test_roundtrip");
	const FString Secret = TEXT("rt_value_üñîçødé_12345");

	Store->Clear(Key);
	FString Loaded;
	TestFalse(TEXT("Load before Save returns false"), Store->Load(Key, Loaded));

	TestTrue(TEXT("Save succeeds"), Store->Save(Key, Secret));
	TestTrue(TEXT("Load after Save succeeds"), Store->Load(Key, Loaded));
	TestEqual(TEXT("Round-trip value matches"), Loaded, Secret);

	Store->Clear(Key);
	FString AfterClear;
	TestFalse(TEXT("Load after Clear returns false"), Store->Load(Key, AfterClear));

	return true;
}

#endif
