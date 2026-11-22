// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"
#include "Templates/SharedPointer.h"
#include "HttpResultCallback.h"

class IHttpRouter;
struct FHttpServerRequest;

class FMantlePlaceLocalTileServer
{
public:
	FMantlePlaceLocalTileServer() = default;
	~FMantlePlaceLocalTileServer();

	FString Start(const FString& RootDir, uint32 Port, FString& OutError);

	void Stop();

	bool IsRunning() const { return bRunning; }
	const FString& GetBaseUrl() const { return BaseUrl; }

private:
	bool HandleRequest(const FHttpServerRequest& Request, const FHttpResultCallback& OnComplete);

	FString ResolveFile(const FString& RequestPath) const;

	TSharedPtr<IHttpRouter> Router;
	FDelegateHandle PreprocessorHandle;
	FString RootDirAbs;
	FString BaseUrl;
	uint32 BoundPort = 0;
	bool bRunning = false;
};
