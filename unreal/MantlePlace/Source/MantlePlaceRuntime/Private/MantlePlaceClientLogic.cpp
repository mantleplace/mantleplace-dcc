// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceClientLogic.h"

#include "HttpModule.h"
#include "Dom/JsonObject.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IPluginManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
	/** Digits first, then only digits, letters, '.', '-' and '+': a semver, with or without a tail. */
	bool LooksLikeVersion(const FString& Version)
	{
		if (Version.IsEmpty() || !FChar::IsDigit(Version[0]))
		{
			return false;
		}
		for (const TCHAR Char : Version)
		{
			if (!FChar::IsAlnum(Char) && Char != TEXT('.') && Char != TEXT('-') && Char != TEXT('+'))
			{
				return false;
			}
		}
		return true;
	}
}

const TCHAR* FMantlePlaceClientLogic::HeaderName()
{
	return TEXT("Mantle-Place-Client");
}

const TCHAR* FMantlePlaceClientLogic::ClientName()
{
	return TEXT("unreal");
}

const TCHAR* FMantlePlaceClientLogic::UpdateRequiredCode()
{
	return TEXT("client_update_required");
}

const TCHAR* FMantlePlaceClientLogic::DownloadUrl()
{
	return TEXT("https://github.com/mantleplace/mantleplace-dcc/releases");
}

FString FMantlePlaceClientLogic::BuildHeaderValue(const FString& PluginVersion)
{
	const FString Trimmed = PluginVersion.TrimStartAndEnd();
	return FString::Printf(TEXT("%s/%s"), ClientName(), LooksLikeVersion(Trimmed) ? *Trimmed : TEXT("unknown"));
}

bool FMantlePlaceClientLogic::ParseClientUpdateRequired(
	int32 HttpStatus, const FString& Body, FMantlePlaceClientUpdate& OutUpdate)
{
	if (HttpStatus != 426)
	{
		return false;
	}

	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Body);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		return false;
	}

	FString Code;
	if (!Root->TryGetStringField(TEXT("code"), Code) || Code != UpdateRequiredCode())
	{
		return false;
	}

	FMantlePlaceClientUpdate Update;
	Root->TryGetStringField(TEXT("version"), Update.SentVersion);
	Root->TryGetStringField(TEXT("minimumVersion"), Update.MinimumVersion);
	Update.DownloadUrl = DownloadUrl();
	Update.Message = BuildUpdateMessage(Update.SentVersion, Update.MinimumVersion);
	OutUpdate = MoveTemp(Update);
	return true;
}

FString FMantlePlaceClientLogic::BuildUpdateMessage(const FString& SentVersion, const FString& MinimumVersion)
{
	FString Message = TEXT("Mantle Place for Unreal needs an update");
	if (!SentVersion.IsEmpty())
	{
		Message += FString::Printf(TEXT(" (this is %s"), *SentVersion);
		Message += MinimumVersion.IsEmpty()
			? FString(TEXT(")"))
			: FString::Printf(TEXT("; the platform now requires %s or newer)"), *MinimumVersion);
	}
	else if (!MinimumVersion.IsEmpty())
	{
		Message += FString::Printf(TEXT(" (the platform now requires %s or newer)"), *MinimumVersion);
	}
	Message += FString::Printf(TEXT(" to keep working. Download the newest plugin: %s"), DownloadUrl());
	return Message;
}

FString FMantlePlaceClientHttp::PluginVersion()
{
	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
	return Plugin.IsValid() ? Plugin->GetDescriptor().VersionName : FString();
}

FString FMantlePlaceClientHttp::HeaderValue()
{
	return FMantlePlaceClientLogic::BuildHeaderValue(PluginVersion());
}

TSharedRef<IHttpRequest, ESPMode::ThreadSafe> FMantlePlaceClientHttp::CreateRequest(const FString& Verb, const FString& Url)
{
	const TSharedRef<IHttpRequest, ESPMode::ThreadSafe> Request = FHttpModule::Get().CreateRequest();
	Request->SetVerb(Verb);
	Request->SetURL(Url);
	Request->SetHeader(FMantlePlaceClientLogic::HeaderName(), HeaderValue());
	return Request;
}
