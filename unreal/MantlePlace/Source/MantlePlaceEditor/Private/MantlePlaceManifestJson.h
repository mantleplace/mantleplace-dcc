// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"

/**
 * The JSON readers the manifest parse and every artifact handler's ParseFields share. Absent keys read
 * as the default, never an error: whether an absent value matters is the block's call, not the reader's.
 */
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

/**
 * Enforce the one glTF orientation this importer supports, the way planet_shape enforces "Flat".
 * Interchange converts Y-up to Z-up unconditionally, so a bundle declaring anything else would
 * import silently wrong — geometry in the world at a plausible-looking transform, rotated 90
 * degrees, with no error anywhere. Absent (empty) is tolerated: that is a bundle that never
 * declared an axis, not one declaring a different one. Returns false with OutError filled.
 */
inline bool CheckUpAxis(const TCHAR* Block, const FString& UpAxis, FString& OutError)
{
	if (UpAxis.IsEmpty() || UpAxis.Equals(TEXT("y"), ESearchCase::IgnoreCase))
	{
		return true;
	}
	OutError = FString::Printf(
		TEXT("Unsupported up_axis \"%s\" on hosts.unreal.%s: this importer converts Y-up glTF only "
		     "(Interchange applies that conversion unconditionally, so any other axis would import "
		     "rotated with no error)."),
		*UpAxis, Block);
	return false;
}
}
