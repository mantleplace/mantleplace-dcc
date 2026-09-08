// Copyright Mantle Place. All Rights Reserved.

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

	Router = FHttpServerModule::Get().GetHttpRouter(Port, /*bFailOnBindFailure*/ true);
	if (!Router.IsValid())
	{
		OutError = FString::Printf(TEXT("Could not bind a local HTTP listener on port %u (already in use?)."), Port);
		return FString();
	}

	// One catch-all preprocessor serves the whole bundle subtree. Simpler than per-tile routes, and the
	// quantized-mesh tiles are arbitrary {z}/{x}/{y}.terrain depth that fixed routes can't express.
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

	// Deliberately NOT StopAllListeners(). UE exposes only a global stop, and the editor now does run
	// another HTTP listener we must not disturb: the auth loopback callback server. Stopping globally
	// tore that down mid-sign-in, and also cleared the module's "listeners enabled" flag, which is what
	// makes a per-port bind check real — so it silently re-broke sign-in's port fallback too.
	//
	// Unregistering the preprocessor above already stops us serving anything. The listener stays bound
	// for the rest of the session; that is the same trade the auth path makes, and there is no
	// per-listener stop to do better with.
	bRunning = false;
	BaseUrl.Reset();
}

FString FMantlePlaceLocalTileServer::ResolveFile(const FString& RequestPath) const
{
	// Which path the request names, and whether it stays inside the served root, is decided in the
	// logic unit — it is a containment check on an attacker-shaped input, and it is asserted by
	// MantlePlace.Import.LocalTileServerLogic. What is left here is the half that needs a disk.
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
	// Loopback QA tool; permissive CORS is harmless and avoids surprises if a browser ever hits it.
	Response->Headers.Add(TEXT("Access-Control-Allow-Origin"), { TEXT("*") });
	// Never let Cesium's persistent request cache (cesium-request-cache.sqlite) serve stale content:
	// successive streams reuse the same loopback URL (.../Terrain/layer.json, {z}/{x}/{y}.terrain?v=1.0.0)
	// for *different* bundles, so a cached response from a prior bundle would otherwise mask the new one.
	// Re-fetching from loopback is effectively free, so disabling caching here is the safe, correct trade.
	Response->Headers.Add(TEXT("Cache-Control"), { TEXT("no-store") });
	OnComplete(MoveTemp(Response));
	return true;
}
