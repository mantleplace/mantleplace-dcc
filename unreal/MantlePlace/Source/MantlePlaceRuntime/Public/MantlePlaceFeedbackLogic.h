// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

/**
 * Pure (engine-/network-free) builder for the Send Feedback link (HPS-56).
 *
 * The plugin carries no feedback UI and no feedback API: the web app's Feedback form is the one
 * place to keep up to date, and the editor only opens the system browser at the link built here:
 *
 *   <base>/feedback?source=dcc-unreal&v=<plugin version>&hv=<host version>[&order=<order id>][&rating=up|down]
 *
 * Nothing else is ever emitted: no email address, no name, no free text, no token. A version that
 * does not match ^[0-9]+(\.[0-9]+){0,3}$ is left out rather than sent raw, `order` rides only on a
 * bundle-scoped action ("Report a problem", or the post-import prompt) and only as a UUID, and
 * `rating` rides only on the post-import prompt "Did this import come in right?" and only as exactly
 * `up` or `down`. Opening the link records nothing: the web form stores the rating when the curator
 * presses send. Kept free of HTTP/UObject dependencies so the automation test exercises it under
 * -nullrhi; the Slate shim only opens the URL returned.
 *
 * The same unit decides the prompt's once-per-bundle rule (ShouldAskAboutImport / RememberAsked);
 * the shim only keeps the list of order ids already asked about.
 */
struct MANTLEPLACERUNTIME_API FMantlePlaceFeedbackLogic
{
	/** The `source` value: `dcc-` plus this host's directory name (HPS-56). */
	static const TCHAR* Source();

	/** True when Version matches ^[0-9]+(\.[0-9]+){0,3}$ (HPS-56: `v` and `hv`). */
	static bool IsValidVersion(const FString& Version);

	/** True when OrderId is a canonical 8-4-4-4-12 hexadecimal UUID (HPS-56: `order`). */
	static bool IsValidOrderId(const FString& OrderId);

	/** True when Rating is exactly `up` or `down` (HPS-56: `rating`). Case-sensitive, no whitespace. */
	static bool IsValidRating(const FString& Rating);

	/**
	 * Build the feedback link. BaseUrl is the web app host (normalised: whitespace and trailing '/'
	 * stripped); a base that is not http(s) yields an empty string, meaning "open nothing".
	 * PluginVersion / HostVersion that fail IsValidVersion are omitted; OrderId is emitted only when
	 * non-empty and IsValidOrderId (pass an empty OrderId for the general action); Rating is emitted
	 * only when IsValidRating (pass an empty Rating for every action but the post-import prompt).
	 */
	static FString BuildFeedbackUrl(const FString& BaseUrl, const FString& PluginVersion, const FString& HostVersion, const FString& OrderId, const FString& Rating = FString());

	/** The most order ids RememberAsked keeps; past it the oldest is forgotten. */
	static constexpr int32 MaxAskedOrders = 500;

	/**
	 * Whether a finished import should ask "Did this import come in right?" (HPS-56): only an import
	 * that succeeded, of a bundle whose OrderId is a UUID (an answer with no bundle to attach to is
	 * not worth the interruption), and not one already in AskedOrderIds (compared ignoring case).
	 */
	static bool ShouldAskAboutImport(bool bImportSucceeded, const FString& OrderId, const TArray<FString>& AskedOrderIds);

	/**
	 * Add OrderId (lower-cased) to AskedOrderIds, unless it is not a UUID or is already there; past
	 * MaxAskedOrders the oldest entries are dropped. Called when the question is shown, not when it is
	 * answered, so a line that is ignored or dismissed does not come back for that bundle.
	 */
	static void RememberAsked(const FString& OrderId, TArray<FString>& AskedOrderIds);
};
