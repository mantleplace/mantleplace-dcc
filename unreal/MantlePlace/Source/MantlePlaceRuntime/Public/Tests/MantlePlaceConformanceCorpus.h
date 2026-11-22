// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace MantlePlaceConformanceCorpus
{
inline const TCHAR* HostKey()
{
	return TEXT("unreal");
}

struct FCase
{
	FString Id;
	FString Group;
	FString File;
	FString Expect;
	FString ErrorContains;
	FString Reason;

	FString Payload;

	TSharedPtr<FJsonObject> PayloadObject;

	TSharedPtr<FJsonObject> Expectations;

	mutable TSet<FString> AssertedKeys;

	mutable TSet<FString> AssertedPaths;

	bool IsAccept() const { return Expect == TEXT("accept"); }
	bool IsReject() const { return Expect == TEXT("reject"); }
	bool IsVector() const { return Expect == TEXT("vector"); }

	FString What(const TCHAR* Detail) const { return FString::Printf(TEXT("[%s] %s"), *Id, Detail); }
};

namespace Detail
{
	inline FString CorpusSearchAnchor()
	{
		const TSharedPtr<IPlugin> ThisPlugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
		return FPaths::ConvertRelativePathToFull(
			ThisPlugin.IsValid() ? ThisPlugin->GetBaseDir() : FPaths::ProjectDir());
	}

	inline FString FindCorpusDir()
	{
		FString Dir = CorpusSearchAnchor();
		FPaths::NormalizeDirectoryName(Dir);

		for (int32 Up = 0; Up < 8 && !Dir.IsEmpty(); ++Up)
		{
			const FString Candidate =
				FPaths::Combine(Dir, TEXT("tools"), TEXT("manifest-conformance"), TEXT("corpus"));
			if (FPaths::FileExists(FPaths::Combine(Candidate, TEXT("index.json"))))
			{
				return Candidate;
			}

			const FString Parent = FPaths::GetPath(Dir);
			if (Parent == Dir)
			{
				break;
			}
			Dir = Parent;
		}
		return FString();
	}

	inline bool LoadJsonObject(const FString& Path, TSharedPtr<FJsonObject>& OutObject, FString& OutText)
	{
		if (!FFileHelper::LoadFileToString(OutText, *Path))
		{
			return false;
		}
		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(OutText);
		return FJsonSerializer::Deserialize(Reader, OutObject) && OutObject.IsValid();
	}

	inline bool IsDocumentationKey(const FString& Key)
	{
		return Key == TEXT("$comment") || Key.EndsWith(TEXT("Note"), ESearchCase::CaseSensitive);
	}

	inline const TCHAR* JsonTypeName(EJson Type)
	{
		switch (Type)
		{
			case EJson::Null:    return TEXT("null");
			case EJson::String:  return TEXT("string");
			case EJson::Number:  return TEXT("number");
			case EJson::Boolean: return TEXT("boolean");
			case EJson::Array:   return TEXT("array");
			case EJson::Object:  return TEXT("object");
			default:             return TEXT("none");
		}
	}

	inline bool HasChildren(const TSharedPtr<FJsonValue>& Value)
	{
		if (!Value.IsValid())
		{
			return false;
		}
		if (Value->Type == EJson::Object)
		{
			const TSharedPtr<FJsonObject>* Object = nullptr;
			return Value->TryGetObject(Object) && Object != nullptr && (*Object)->Values.Num() > 0;
		}
		if (Value->Type == EJson::Array)
		{
			const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
			return Value->TryGetArray(Array) && Array != nullptr && Array->Num() > 0;
		}
		return false;
	}

	inline void CollectNestedLeaves(
		const TSharedPtr<FJsonValue>& Value,
		const FString& Path,
		TArray<TPair<FString, EJson>>& OutLeaves)
	{
		if (!HasChildren(Value))
		{
			OutLeaves.Emplace(Path, Value.IsValid() ? Value->Type : EJson::None);
			return;
		}

		if (Value->Type == EJson::Object)
		{
			const TSharedPtr<FJsonObject>* Object = nullptr;
			Value->TryGetObject(Object);
			for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*Object)->Values)
			{
				if (IsDocumentationKey(Pair.Key))
				{
					continue;
				}
				CollectNestedLeaves(Pair.Value, Path + TEXT(".") + Pair.Key, OutLeaves);
			}
			return;
		}

		const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
		Value->TryGetArray(Array);
		for (int32 Index = 0; Index < Array->Num(); ++Index)
		{
			CollectNestedLeaves((*Array)[Index], FString::Printf(TEXT("%s[%d]"), *Path, Index), OutLeaves);
		}
	}

	inline bool RecordTypedRead(
		const FCase& Case,
		const FString& Path,
		const TSharedPtr<FJsonValue>& Value,
		EJson Wanted)
	{
		if (!Value.IsValid())
		{
			return false;
		}
		if (Value->Type == EJson::Null)
		{
			Case.AssertedPaths.Add(Path);
			return false;
		}
		if (Value->Type != Wanted)
		{
			return false;
		}
		Case.AssertedPaths.Add(Path);
		return true;
	}
}

inline FString PinnedManifestVersion()
{
	const FString Root = Detail::FindCorpusDir();
	if (Root.IsEmpty())
	{
		return FString();
	}
	TSharedPtr<FJsonObject> Index;
	FString Text;
	if (!Detail::LoadJsonObject(FPaths::Combine(Root, TEXT("index.json")), Index, Text))
	{
		return FString();
	}
	FString Version;
	if (!Index->TryGetStringField(TEXT("manifestVersion"), Version))
	{
		double NumericVersion = 0.0;
		if (Index->TryGetNumberField(TEXT("manifestVersion"), NumericVersion))
		{
			Version = FString::FromInt(static_cast<int32>(NumericVersion));
		}
	}
	return Version;
}

inline bool LoadGroupFromDir(const FString& Root, const TCHAR* Group, TArray<FCase>& OutCases, FString& OutError)
{
	OutCases.Reset();
	OutError.Reset();

	TSharedPtr<FJsonObject> Index;
	FString IndexText;
	if (!Detail::LoadJsonObject(FPaths::Combine(Root, TEXT("index.json")), Index, IndexText))
	{
		OutError = FString::Printf(TEXT("corpus index.json under '%s' is unreadable or not valid JSON"), *Root);
		return false;
	}

	const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
	if (!Index->TryGetArrayField(TEXT("cases"), Cases) || Cases == nullptr)
	{
		OutError = TEXT("corpus index.json has no `cases` array");
		return false;
	}

	TArray<FString> Problems;
	TSet<FString> SeenIds;
	for (const TSharedPtr<FJsonValue>& Value : *Cases)
	{
		const TSharedPtr<FJsonObject>* Entry = nullptr;
		if (!Value.IsValid() || !Value->TryGetObject(Entry) || Entry == nullptr)
		{
			continue;
		}
		const TSharedPtr<FJsonObject>& Object = *Entry;

		FString CaseGroup;
		Object->TryGetStringField(TEXT("group"), CaseGroup);
		if (CaseGroup != Group)
		{
			continue;
		}

		const TSharedPtr<FJsonValue> AppliesToValue = Object->TryGetField(TEXT("appliesTo"));
		if (AppliesToValue.IsValid() && AppliesToValue->Type == EJson::String
			&& AppliesToValue->AsString() != HostKey())
		{
			continue;
		}

		FCase Case;
		Case.Group = CaseGroup;
		Object->TryGetStringField(TEXT("id"), Case.Id);
		Object->TryGetStringField(TEXT("file"), Case.File);
		Object->TryGetStringField(TEXT("expect"), Case.Expect);
		Object->TryGetStringField(TEXT("errorContains"), Case.ErrorContains);
		Object->TryGetStringField(TEXT("reason"), Case.Reason);

		if (SeenIds.Contains(Case.Id))
		{
			Problems.Add(FString::Printf(
				TEXT("case id '%s' appears more than once in the index — the later entry is invisible ")
				TEXT("to id-dispatched suites (HPS-46)"),
				*Case.Id));
			continue;
		}
		SeenIds.Add(Case.Id);

		const TSharedPtr<FJsonObject>* ExpectationsPtr = nullptr;
		if (Object->TryGetObjectField(TEXT("expectations"), ExpectationsPtr) && ExpectationsPtr != nullptr)
		{
			Case.Expectations = *ExpectationsPtr;
		}

		const FString VectorPath = FPaths::Combine(Root, Case.File);
		if (!FFileHelper::LoadFileToString(Case.Payload, *VectorPath))
		{
			Problems.Add(FString::Printf(TEXT("case '%s' names a missing vector file: %s"), *Case.Id, *VectorPath));
			continue;
		}

		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(Case.Payload);
		TSharedPtr<FJsonObject> Parsed;
		if (FJsonSerializer::Deserialize(Reader, Parsed) && Parsed.IsValid())
		{
			Case.PayloadObject = Parsed;
		}
		else
		{
			bool bDeclaredMalformed = false;
			Object->TryGetBoolField(TEXT("malformedJson"), bDeclaredMalformed);
			if (!bDeclaredMalformed)
			{
				Problems.Add(FString::Printf(
					TEXT("case '%s' file is not valid JSON and the index does not declare malformedJson — ")
					TEXT("undeclared rot, not a deliberate fixture (HPS-46): %s"),
					*Case.Id, *VectorPath));
				continue;
			}
		}

		OutCases.Add(MoveTemp(Case));
	}

	if (Problems.Num() > 0)
	{
		OutError = FString::Join(Problems, TEXT("\n"));
		return false;
	}

	if (OutCases.Num() == 0)
	{
		OutError = FString::Printf(
			TEXT("corpus group '%s' resolved to zero cases for host '%s' — a suite that asserts nothing ")
			TEXT("reports green for the wrong reason (HPS-40)"),
			Group, HostKey());
		return false;
	}

	return true;
}

inline bool LoadGroup(const TCHAR* Group, TArray<FCase>& OutCases, FString& OutError)
{
	OutCases.Reset();

	const FString Root = Detail::FindCorpusDir();
	if (Root.IsEmpty())
	{
		OutError = FString::Printf(
			TEXT("could not locate tools/manifest-conformance/corpus/index.json by walking up from '%s'. ")
			TEXT("The shared corpus is checked into this repo; a working tree without it cannot assert ")
			TEXT("HPS-40 conformance."),
			*Detail::CorpusSearchAnchor());
		return false;
	}

	return LoadGroupFromDir(Root, Group, OutCases, OutError);
}

inline FString FindCorpusRoot()
{
	return Detail::FindCorpusDir();
}

inline bool MissingCorpusSkips(const FString& CiVariable, bool bPackagedPlugin)
{
	return bPackagedPlugin || CiVariable.IsEmpty();
}

namespace Detail
{
	inline bool IsPackagedPlugin()
	{
		const TSharedPtr<IPlugin> ThisPlugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
		return ThisPlugin.IsValid() && ThisPlugin->GetDescriptor().bInstalled;
	}
}

inline bool SkipWithoutCorpus(FAutomationTestBase& Test)
{
	if (!Detail::FindCorpusDir().IsEmpty()
		|| !MissingCorpusSkips(FPlatformMisc::GetEnvironmentVariable(TEXT("CI")), Detail::IsPackagedPlugin()))
	{
		return false;
	}

	Test.AddWarning(FString::Printf(
		TEXT("Conformance corpus not present (tools/manifest-conformance/corpus/index.json, looked for ")
		TEXT("above '%s'); skipped. A release zip does not ship the corpus; from the repository this ")
		TEXT("test runs."),
		*Detail::CorpusSearchAnchor()));
	return true;
}

inline bool UnindexedCaseFiles(const FString& Root, TArray<FString>& OutFiles, FString& OutError)
{
	OutFiles.Reset();
	OutError.Reset();

	TSharedPtr<FJsonObject> Index;
	FString IndexText;
	if (!Detail::LoadJsonObject(FPaths::Combine(Root, TEXT("index.json")), Index, IndexText))
	{
		OutError = FString::Printf(TEXT("corpus index.json under '%s' is unreadable or not valid JSON"), *Root);
		return false;
	}

	TSet<FString> Indexed;
	const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
	if (Index->TryGetArrayField(TEXT("cases"), Cases) && Cases != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Value : *Cases)
		{
			const TSharedPtr<FJsonObject>* Entry = nullptr;
			FString File;
			if (Value.IsValid() && Value->TryGetObject(Entry) && Entry != nullptr
				&& (*Entry)->TryGetStringField(TEXT("file"), File))
			{
				Indexed.Add(File.Replace(TEXT("\\"), TEXT("/")));
			}
		}
	}

	FString NormalRoot = FPaths::ConvertRelativePathToFull(Root);
	FPaths::NormalizeDirectoryName(NormalRoot);

	TArray<FString> OnDisk;
	IFileManager::Get().FindFilesRecursive(OnDisk, *NormalRoot, TEXT("*.json"), true, false);
	for (FString& Path : OnDisk)
	{
		FPaths::NormalizeFilename(Path);
		FString Relative = Path;
		FPaths::MakePathRelativeTo(Relative, *(NormalRoot + TEXT("/")));
		if (Relative == TEXT("index.json"))
		{
			continue;
		}

		bool bNestedCorpus = false;
		TArray<FString> Segments;
		Relative.ParseIntoArray(Segments, TEXT("/"), true);
		FString Prefix;
		for (int32 Depth = 0; Depth < Segments.Num() - 1; ++Depth)
		{
			Prefix = Prefix.IsEmpty() ? Segments[Depth] : (Prefix + TEXT("/") + Segments[Depth]);
			if (FPaths::FileExists(FPaths::Combine(NormalRoot, Prefix, TEXT("index.json"))))
			{
				bNestedCorpus = true;
				break;
			}
		}
		if (bNestedCorpus || Indexed.Contains(Relative))
		{
			continue;
		}
		OutFiles.Add(Relative);
	}
	return true;
}

inline bool WantsString(const FCase& Case, const TCHAR* Key, FString& OutValue)
{
	const TSharedPtr<FJsonValue> Value =
		Case.Expectations.IsValid() ? Case.Expectations->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::String)
	{
		return false;
	}
	OutValue = Value->AsString();
	Case.AssertedKeys.Add(Key);
	return true;
}

inline bool WantsBool(const FCase& Case, const TCHAR* Key, bool& OutValue)
{
	const TSharedPtr<FJsonValue> Value =
		Case.Expectations.IsValid() ? Case.Expectations->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Boolean)
	{
		return false;
	}
	OutValue = Value->AsBool();
	Case.AssertedKeys.Add(Key);
	return true;
}

inline bool WantsNumber(const FCase& Case, const TCHAR* Key, double& OutValue)
{
	const TSharedPtr<FJsonValue> Value =
		Case.Expectations.IsValid() ? Case.Expectations->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Number)
	{
		return false;
	}
	OutValue = Value->AsNumber();
	Case.AssertedKeys.Add(Key);
	return true;
}

inline bool WantsInt(const FCase& Case, const TCHAR* Key, int32& OutValue)
{
	double Number = 0.0;
	if (!WantsNumber(Case, Key, Number))
	{
		return false;
	}
	OutValue = static_cast<int32>(Number);
	return true;
}

inline bool WantsNumbers(const FCase& Case, const TCHAR* Key, int32 ExpectedNum, TArray<double>& OutValues)
{
	OutValues.Reset();
	if (!Case.Expectations.IsValid())
	{
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (!Case.Expectations->TryGetArrayField(Key, Array) || Array == nullptr || Array->Num() != ExpectedNum)
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Array)
	{
		if (!Value.IsValid() || Value->Type != EJson::Number)
		{
			OutValues.Reset();
			return false;
		}
		OutValues.Add(Value->AsNumber());
	}
	Case.AssertedKeys.Add(Key);
	for (int32 Index = 0; Index < Array->Num(); ++Index)
	{
		Case.AssertedPaths.Add(FString::Printf(TEXT("%s[%d]"), Key, Index));
	}
	return true;
}

inline bool WantsStringArray(const FCase& Case, const TCHAR* Key, TArray<FString>& OutValues)
{
	OutValues.Reset();
	if (!Case.Expectations.IsValid())
	{
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (!Case.Expectations->TryGetArrayField(Key, Array) || Array == nullptr)
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Array)
	{
		if (!Value.IsValid() || Value->Type != EJson::String)
		{
			OutValues.Reset();
			return false;
		}
		OutValues.Add(Value->AsString());
	}
	Case.AssertedKeys.Add(Key);
	for (int32 Index = 0; Index < Array->Num(); ++Index)
	{
		Case.AssertedPaths.Add(FString::Printf(TEXT("%s[%d]"), Key, Index));
	}
	return true;
}

inline bool WantsObjectRows(const FCase& Case, const TCHAR* Key, TArray<TSharedPtr<FJsonObject>>& OutRows)
{
	OutRows.Reset();
	if (!Case.Expectations.IsValid())
	{
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (!Case.Expectations->TryGetArrayField(Key, Array) || Array == nullptr)
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Array)
	{
		const TSharedPtr<FJsonObject>* Object = nullptr;
		if (!Value.IsValid() || !Value->TryGetObject(Object) || Object == nullptr)
		{
			OutRows.Reset();
			return false;
		}
		OutRows.Add(*Object);
	}
	Case.AssertedKeys.Add(Key);
	return true;
}

inline double ToleranceOr(const FCase& Case, const TCHAR* Key, double Fallback)
{
	double Value = Fallback;
	WantsNumber(Case, Key, Value);
	return Value;
}

inline FString ExpectPath(const FString& RowPath, const TCHAR* Key)
{
	return RowPath.IsEmpty() ? FString(Key) : FString::Printf(TEXT("%s.%s"), *RowPath, Key);
}

inline bool ExpectRowString(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, FString& OutValue)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Detail::RecordTypedRead(Case, ExpectPath(RowPath, Key), Value, EJson::String))
	{
		return false;
	}
	OutValue = Value->AsString();
	return true;
}

inline bool ExpectRowBool(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, bool& bOutValue)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Detail::RecordTypedRead(Case, ExpectPath(RowPath, Key), Value, EJson::Boolean))
	{
		return false;
	}
	bOutValue = Value->AsBool();
	return true;
}

inline bool ExpectRowNumber(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, double& OutValue)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Detail::RecordTypedRead(Case, ExpectPath(RowPath, Key), Value, EJson::Number))
	{
		return false;
	}
	OutValue = Value->AsNumber();
	return true;
}

inline bool ExpectRowVersion(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, FString& OutValue)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (Detail::RecordTypedRead(Case, ExpectPath(RowPath, Key), Value, EJson::String))
	{
		OutValue = Value->AsString();
		return true;
	}
	if (Value.IsValid() && Value->Type == EJson::Number)
	{
		Case.AssertedPaths.Add(ExpectPath(RowPath, Key));
		OutValue = FString::FromInt(static_cast<int32>(Value->AsNumber()));
		return true;
	}
	return false;
}

inline bool ExpectRowObject(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, TSharedPtr<FJsonObject>& OutObject)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Object)
	{
		return false;
	}
	const TSharedPtr<FJsonObject>* Object = nullptr;
	if (!Value->TryGetObject(Object) || Object == nullptr)
	{
		return false;
	}
	if (!Detail::HasChildren(Value))
	{
		Case.AssertedPaths.Add(ExpectPath(RowPath, Key));
	}
	OutObject = *Object;
	return true;
}

inline bool ExpectRowArray(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, const TArray<TSharedPtr<FJsonValue>>*& OutArray)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Array)
	{
		return false;
	}
	if (!Value->TryGetArray(OutArray) || OutArray == nullptr)
	{
		return false;
	}
	if (!Detail::HasChildren(Value))
	{
		Case.AssertedPaths.Add(ExpectPath(RowPath, Key));
	}
	return true;
}

inline bool ExpectElementString(
	const FCase& Case, const FString& ElementPath, const TSharedPtr<FJsonValue>& Value,
	FString& OutValue)
{
	if (!Detail::RecordTypedRead(Case, ElementPath, Value, EJson::String))
	{
		return false;
	}
	OutValue = Value->AsString();
	return true;
}

inline bool ExpectElementNumber(
	const FCase& Case, const FString& ElementPath, const TSharedPtr<FJsonValue>& Value,
	double& OutValue)
{
	if (!Detail::RecordTypedRead(Case, ElementPath, Value, EJson::Number))
	{
		return false;
	}
	OutValue = Value->AsNumber();
	return true;
}

inline TSharedPtr<FJsonObject> ExpectElementObject(const TSharedPtr<FJsonValue>& Value)
{
	const TSharedPtr<FJsonObject>* Object = nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Object || !Value->TryGetObject(Object) || Object == nullptr)
	{
		return nullptr;
	}
	return *Object;
}

inline TArray<TPair<FString, EJson>> NestedExpectationLeaves(const FCase& Case)
{
	TArray<TPair<FString, EJson>> Leaves;
	if (!Case.Expectations.IsValid())
	{
		return Leaves;
	}
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : Case.Expectations->Values)
	{
		if (!Detail::IsDocumentationKey(Pair.Key) && Detail::HasChildren(Pair.Value))
		{
			Detail::CollectNestedLeaves(Pair.Value, Pair.Key, Leaves);
		}
	}
	Leaves.Sort([](const TPair<FString, EJson>& Left, const TPair<FString, EJson>& Right)
	{
		return Left.Key < Right.Key;
	});
	return Leaves;
}

inline TArray<FString> UnassertedNestedExpectations(const FCase& Case)
{
	TArray<FString> Problems;
	for (const TPair<FString, EJson>& Leaf : NestedExpectationLeaves(Case))
	{
		if (Case.AssertedPaths.Contains(Leaf.Key))
		{
			continue;
		}
		Problems.Add(FString::Printf(
			TEXT("'%s' is a nested expectation this host never read (%s). Nothing asserts it, or it ")
			TEXT("is declared with a type the accessor rejects (HPS-46b) — teach the suite to ")
			TEXT("consume it, do not delete it from the corpus"),
			*Leaf.Key,
			Detail::JsonTypeName(Leaf.Value)));
	}
	return Problems;
}

inline TArray<FString> UnassertedExpectations(const FCase& Case, TConstArrayView<const TCHAR*> Consumed)
{
	TArray<FString> Problems;
	if (!Case.Expectations.IsValid())
	{
		return Problems;
	}
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : Case.Expectations->Values)
	{
		if (Case.AssertedKeys.Contains(Pair.Key))
		{
			continue;
		}

		bool bKnown = false;
		for (const TCHAR* Known : Consumed)
		{
			if (Pair.Key == Known)
			{
				bKnown = true;
				break;
			}
		}

		Problems.Add(bKnown
			? FString::Printf(
				TEXT("'%s' is declared with an unexpected JSON type (%s), so this host read nothing ")
				TEXT("from it (HPS-46)"),
				*Pair.Key,
				Detail::JsonTypeName(Pair.Value.IsValid() ? Pair.Value->Type : EJson::None))
			: FString::Printf(
				TEXT("'%s' is an expectation this host does not assert. Teach the suite to consume it ")
				TEXT("(HPS-46) — do not delete it from the corpus"),
				*Pair.Key));
	}
	return Problems;
}

inline TArray<TSharedPtr<FJsonObject>> Rows(const FCase& Case, const TCHAR* ArrayField)
{
	TArray<TSharedPtr<FJsonObject>> Out;
	if (!Case.PayloadObject.IsValid())
	{
		return Out;
	}
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (!Case.PayloadObject->TryGetArrayField(ArrayField, Array) || Array == nullptr)
	{
		return Out;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Array)
	{
		const TSharedPtr<FJsonObject>* Object = nullptr;
		if (Value.IsValid() && Value->TryGetObject(Object) && Object != nullptr)
		{
			Out.Add(*Object);
		}
	}
	return Out;
}

inline TArray<FString> UndrivenCases(const TArray<FCase>& Cases, const TSet<FString>& Driven)
{
	TArray<FString> Missing;
	for (const FCase& Case : Cases)
	{
		if (!Driven.Contains(Case.Id))
		{
			Missing.Add(Case.Id);
		}
	}
	return Missing;
}

inline const FCase* FindCase(const TArray<FCase>& Cases, const TCHAR* Id)
{
	return Cases.FindByPredicate([Id](const FCase& Case) { return Case.Id == Id; });
}

inline FString RowString(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key)
{
	FString Value;
	if (Row.IsValid())
	{
		Row->TryGetStringField(Key, Value);
	}
	return Value;
}

inline double RowNumber(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key, double Fallback = 0.0)
{
	double Value = Fallback;
	if (Row.IsValid())
	{
		Row->TryGetNumberField(Key, Value);
	}
	return Value;
}

inline bool RowBool(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key, bool bFallback = false)
{
	bool bValue = bFallback;
	if (Row.IsValid())
	{
		Row->TryGetBoolField(Key, bValue);
	}
	return bValue;
}

inline TArray<FString> RowStrings(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key)
{
	TArray<FString> Out;
	if (Row.IsValid())
	{
		Row->TryGetStringArrayField(Key, Out);
	}
	return Out;
}

inline FString RowBodyAsText(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key = TEXT("body"))
{
	if (!Row.IsValid())
	{
		return FString();
	}

	if (RowBool(Row, TEXT("raw")))
	{
		return RowString(Row, Key);
	}

	const TSharedPtr<FJsonObject>* Body = nullptr;
	if (!Row->TryGetObjectField(Key, Body) || Body == nullptr)
	{
		return FString();
	}

	FString Text;
	const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Text);
	FJsonSerializer::Serialize(Body->ToSharedRef(), Writer);
	return Text;
}

}

#endif
