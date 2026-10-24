// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/**
 * Pure (engine-/network-free) builder for the Send Feedback link (HPS-56).
 *
 * The plugin carries no feedback UI and no feedback API: the web app's Feedback form is the one
 * place to keep up to date, and the editor only opens the system browser at the link built here:
 *
 *   <base>/feedback?source=dcc-unreal&v=<plugin version>&hv=<host version>[&order=<order id>]
 *
 * Nothing else is ever emitted: no email address, no name, no free text, no token. A version that
 * does not match ^[0-9]+(\.[0-9]+){0,3}$ is left out rather than sent raw, and `order` rides only
 * on a bundle-scoped "Report a problem" and only as a UUID. Kept free of HTTP/UObject dependencies
 * so the automation test exercises it under -nullrhi; the Slate shim only opens the URL returned.
 */
struct MANTLEPLACERUNTIME_API FMantlePlaceFeedbackLogic
{
	/** The `source` value: `dcc-` plus this host's directory name (HPS-56). */
	static const TCHAR* Source();

	/** True when Version matches ^[0-9]+(\.[0-9]+){0,3}$ (HPS-56: `v` and `hv`). */
	static bool IsValidVersion(const FString& Version);

	/** True when OrderId is a canonical 8-4-4-4-12 hexadecimal UUID (HPS-56: `order`). */
	static bool IsValidOrderId(const FString& OrderId);

	/**
	 * Build the feedback link. BaseUrl is the web app host (normalised: whitespace and trailing '/'
	 * stripped); a base that is not http(s) yields an empty string, meaning "open nothing".
	 * PluginVersion / HostVersion that fail IsValidVersion are omitted; OrderId is emitted only when
	 * non-empty and IsValidOrderId (pass an empty OrderId for the general action).
	 */
	static FString BuildFeedbackUrl(const FString& BaseUrl, const FString& PluginVersion, const FString& HostVersion, const FString& OrderId);
};
