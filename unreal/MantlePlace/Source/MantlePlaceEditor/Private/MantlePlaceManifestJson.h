// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"

namespace MantlePlaceManifestJson
{
inline const TSharedPtr<FJsonObject>* GetObject(const TSharedPtr<FJsonObject>& Parent, const TCHAR* Field)
{
	if (!Parent.IsValid())
	{
		return nullptr;
	}
	const TSharedPtr<FJsonObject>* Out = nullptr;
	return Parent->TryGetObjectField(Field, Out) ? Out : nullptr;
}

inline double GetNumber(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, double Default = 0.0)
{
	double Value = Default;
	if (Obj.IsValid())
	{
		Obj->TryGetNumberField(Field, Value);
	}
	return Value;
}

inline int32 GetInt(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, int32 Default = 0)
{
	return static_cast<int32>(FMath::RoundToDouble(GetNumber(Obj, Field, static_cast<double>(Default))));
}

inline FString GetString(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field)
{
	FString Value;
	if (Obj.IsValid())
	{
		Obj->TryGetStringField(Field, Value);
	}
	return Value;
}

inline bool GetBool(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field, bool Default)
{
	bool Value = Default;
	if (Obj.IsValid())
	{
		Obj->TryGetBoolField(Field, Value);
	}
	return Value;
}

inline TArray<FString> GetStrings(const TSharedPtr<FJsonObject>& Obj, const TCHAR* Field)
{
	TArray<FString> Values;
	if (Obj.IsValid())
	{
		Obj->TryGetStringArrayField(Field, Values);
	}
	return Values;
}

inline TSharedPtr<FJsonObject> FindFileEntry(const TSharedPtr<FJsonObject>& Root, const FString& ArchivePath)
{
	const TSharedPtr<FJsonObject>* EntriesPtr = GetObject(Root, TEXT("file_entries"));
	if (EntriesPtr == nullptr || ArchivePath.IsEmpty())
	{
		return nullptr;
	}
	const TSharedPtr<FJsonObject>* Exact = nullptr;
	if ((*EntriesPtr)->TryGetObjectField(ArchivePath, Exact) && Exact != nullptr)
	{
		return *Exact;
	}
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*EntriesPtr)->Values)
	{
		const TSharedPtr<FJsonObject>* Entry = nullptr;
		if (Pair.Key.Equals(ArchivePath, ESearchCase::IgnoreCase) && Pair.Value.IsValid()
		    && Pair.Value->TryGetObject(Entry) && Entry != nullptr)
		{
			return *Entry;
		}
	}
	return nullptr;
}

inline bool CheckUpAxis(const TCHAR* Block, const FString& UpAxis, FString& OutError)
{
	if (UpAxis.IsEmpty() || UpAxis.Equals(TEXT("y"), ESearchCase::IgnoreCase))
	{
		return true;
	}
	OutError = FString::Printf(
		TEXT("Unsupported up_axis \"%s\" on hosts.unreal.%s: this plugin converts Y-up glTF only "
		     "(Interchange applies that conversion unconditionally, so any other axis would import "
		     "rotated with no error)."),
		*UpAxis, Block);
	return false;
}
}
