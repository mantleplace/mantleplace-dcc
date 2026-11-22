// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#include "MantlePlaceLocalTileServer.h"

#include "MantlePlaceLocalTileServerLogic.h"

#include "HttpServerModule.h"
#include "HttpServerResponse.h"
#include "HttpServerRequest.h"
#include "HttpServerConstants.h"
#include "IHttpRouter.h"
#include "HttpPath.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "HAL/PlatformFileManager.h"
#include "GenericPlatform/GenericPlatformFile.h"

FMantlePlaceLocalTileServer::~FMantlePlaceLocalTileServer()
{
	Stop();
}

FString FMantlePlaceLocalTileServer::Start(const FString& RootDir, uint32 Port, FString& OutError)
{
	Stop();

	RootDirAbs = FPaths::ConvertRelativePathToFull(RootDir);
	FPaths::NormalizeDirectoryName(RootDirAbs);
	if (!FPlatformFileManager::Get().GetPlatformFile().DirectoryExists(*RootDirAbs))
	{
		OutError = FString::Printf(TEXT("Serve root does not exist: %s"), *RootDirAbs);
		return FString();
	}

	if (!FModuleManager::Get().IsModuleLoaded(TEXT("HTTPServer")))
	{
		FModuleManager::Get().LoadModule(TEXT("HTTPServer"));
	}

	Router = FHttpServerModule::Get().GetHttpRouter(Port, true);
	if (!Router.IsValid())
	{
		OutError = FString::Printf(TEXT("Could not bind a local HTTP listener on port %u (already in use?)."), Port);
		return FString();
	}

	PreprocessorHandle = Router->RegisterRequestPreprocessor(
		FHttpRequestHandler::CreateRaw(this, &FMantlePlaceLocalTileServer::HandleRequest));

	FHttpServerModule::Get().StartAllListeners();

	BoundPort = Port;
	bRunning = true;
	BaseUrl = FString::Printf(TEXT("http://127.0.0.1:%u"), Port);
	return BaseUrl;
}

void FMantlePlaceLocalTileServer::Stop()
{
	if (Router.IsValid() && PreprocessorHandle.IsValid())
	{
		Router->UnregisterRequestPreprocessor(PreprocessorHandle);
	}
	PreprocessorHandle.Reset();
	Router.Reset();

	bRunning = false;
	BaseUrl.Reset();
}

FString FMantlePlaceLocalTileServer::ResolveFile(const FString& RequestPath) const
{
	const FString Candidate = FMantlePlaceLocalTileServerLogic::ResolveUnderRoot(RootDirAbs, RequestPath);
	if (Candidate.IsEmpty())
	{
		return FString();
	}
	if (!FPlatformFileManager::Get().GetPlatformFile().FileExists(*Candidate))
	{
		return FString();
	}
	return Candidate;
}

bool FMantlePlaceLocalTileServer::HandleRequest(const FHttpServerRequest& Request, const FHttpResultCallback& OnComplete)
{
	const FString& Path = Request.FullPath.GetPath();
	const FString File = ResolveFile(Path);
	if (File.IsEmpty())
	{
		OnComplete(FHttpServerResponse::Error(
			EHttpServerResponseCodes::NotFound, TEXT("not_found"),
			FString::Printf(TEXT("No such file: %s"), *Path)));
		return true;
	}

	TArray<uint8> Bytes;
	if (!FFileHelper::LoadFileToArray(Bytes, *File))
	{
		OnComplete(FHttpServerResponse::Error(
			EHttpServerResponseCodes::ServerError, TEXT("read_failed"), TEXT("Could not read file.")));
		return true;
	}

	bool bGzip = false;
	const FString ContentType = FMantlePlaceLocalTileServerLogic::ContentTypeFor(File, bGzip);
	TUniquePtr<FHttpServerResponse> Response = FHttpServerResponse::Create(MoveTemp(Bytes), ContentType);
	Response->Code = EHttpServerResponseCodes::Ok;
	if (bGzip)
	{
		Response->Headers.Add(TEXT("Content-Encoding"), { TEXT("gzip") });
	}
	Response->Headers.Add(TEXT("Access-Control-Allow-Origin"), { TEXT("*") });
	Response->Headers.Add(TEXT("Cache-Control"), { TEXT("no-store") });
	OnComplete(MoveTemp(Response));
	return true;
}
