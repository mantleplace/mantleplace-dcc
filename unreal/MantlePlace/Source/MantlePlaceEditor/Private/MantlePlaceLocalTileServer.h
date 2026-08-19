// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Templates/SharedPointer.h"
#include "HttpResultCallback.h" // FHttpResultCallback typedef (+ FHttpServerResponse)

class IHttpRouter;
struct FHttpServerRequest;

/**
 * A tiny localhost static-file server for streaming an extracted ETL bundle into Cesium for Unreal.
 *
 * Cesium for Unreal fetches terrain/imagery over HTTP, so to let it read the user's *own* downloaded
 * bundle (download-to-own, never streamed from the platform) we host the extracted bundle directory on
 * 127.0.0.1. It serves the bundle's Cesium-ready artifacts verbatim:
 *   - `Terrain/layer.json` + `Terrain/{z}/{x}/{y}.terrain` (quantized-mesh; `.terrain` is gzip on disk,
 *     so it is served with `Content-Encoding: gzip` and the quantized-mesh content-type), and
 *   - `Imagery/Imagery.png` (the AOI raster) as a single-tile raster overlay source.
 *
 * Editor-only, QA/preview tool — bound to loopback, no auth, low traffic. One server instance per
 * served root; Start() is idempotent-ish (Stop() any prior instance first).
 */
class FMantlePlaceLocalTileServer
{
public:
	FMantlePlaceLocalTileServer() = default;
	~FMantlePlaceLocalTileServer();

	/**
	 * Start serving `RootDir` on 127.0.0.1:`Port`. Returns the base URL (e.g. "http://127.0.0.1:8088")
	 * on success, empty on failure (OutError set). Files are resolved relative to RootDir; requests that
	 * escape RootDir are rejected.
	 */
	FString Start(const FString& RootDir, uint32 Port, FString& OutError);

	/** Stop the listener + unregister the handler. Safe to call when not running. */
	void Stop();

	bool IsRunning() const { return bRunning; }
	const FString& GetBaseUrl() const { return BaseUrl; }

private:
	/** Catch-all request handler (registered as a preprocessor): serves any file under RootDir. */
	bool HandleRequest(const FHttpServerRequest& Request, const FHttpResultCallback& OnComplete);

	/** Resolve a request path to an absolute on-disk file under RootDir, or empty if it escapes/missing. */
	FString ResolveFile(const FString& RequestPath) const;

	TSharedPtr<IHttpRouter> Router;
	FDelegateHandle PreprocessorHandle;
	FString RootDirAbs;
	FString BaseUrl;
	uint32 BoundPort = 0;
	bool bRunning = false;
};
