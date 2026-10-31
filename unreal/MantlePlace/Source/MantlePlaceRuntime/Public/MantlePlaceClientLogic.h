// Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

#pragma once

#include "CoreMinimal.h"

class IHttpRequest;

/**
 * What the platform said when it refused this plugin for being too old: an HTTP 426 carrying the
 * code `client_update_required`. Not a sign-in failure and not a network failure; the curator's
 * session is fine and so is their connection, and the fix is to install a newer plugin.
 */
struct FMantlePlaceClientUpdate
{
	/** A sentence for the curator, ready to show: what is out of date and where to get the update. */
	FString Message;

	/** The version this plugin sent (what the platform echoed back as `version`). */
	FString SentVersion;

	/** The lowest version the platform serves to this client (`minimumVersion`); empty if not stated. */
	FString MinimumVersion;

	/** Where the curator gets the newer plugin. */
	FString DownloadUrl;
};

/** Native broadcast that the platform wants a newer plugin; C++/Slate observers bind it with AddUObject. */
DECLARE_MULTICAST_DELEGATE_OneParam(FMantlePlaceOnClientUpdateRequiredNative, const FMantlePlaceClientUpdate& /*Update*/);

/**
 * Pure (engine-/network-free) half of the client header contract.
 *
 * Every call to the Platform API names its client and version in the `Mantle-Place-Client` header,
 * `unreal/<plugin version>`. A platform that has set a minimum version for this client answers a
 * call below it with HTTP 426 and the code `client_update_required`, so the plugin can say "update"
 * instead of showing an error. The spec both ends follow is served at
 * https://mantle.place/api/v1/openapi.json (header `Mantle-Place-Client`, response
 * `ClientUpdateRequired`). Kept free of HTTP/UObject dependencies so the automation test exercises it
 * headless; FMantlePlaceClientHttp below is the thin impure shim.
 */
struct MANTLEPLACERUNTIME_API FMantlePlaceClientLogic
{
	/** The header name: `Mantle-Place-Client`. */
	static const TCHAR* HeaderName();

	/** This client's name in the header and in the platform's table of minimums: `unreal`. */
	static const TCHAR* ClientName();

	/** The `code` of the 426 body: `client_update_required`. */
	static const TCHAR* UpdateRequiredCode();

	/** Where a curator gets a newer plugin: the repository's Releases page, which the README's Install section names. */
	static const TCHAR* DownloadUrl();

	/**
	 * The header value for a plugin version: `unreal/<version>`. A version that is empty or holds
	 * anything but digits, dots and a semver tail is sent as `unknown`, which the platform accepts and
	 * records as an unknown client; a malformed value would be read the same way, so the header is
	 * always present and never carries free text.
	 */
	static FString BuildHeaderValue(const FString& PluginVersion);

	/**
	 * Is this response the platform asking for a newer plugin? True only for HTTP 426 whose JSON body
	 * has `code` equal to `client_update_required`; a bare 426 from something in front of the platform
	 * is not it. Fills OutUpdate on true.
	 */
	static bool ParseClientUpdateRequired(int32 HttpStatus, const FString& Body, FMantlePlaceClientUpdate& OutUpdate);

	/** The curator-facing sentence for an update: names both versions when the platform gave them. */
	static FString BuildUpdateMessage(const FString& SentVersion, const FString& MinimumVersion);
};

/**
 * Impure half: the one place a Platform API request is made, so none can leave without the header.
 * The auth and vault clients create every request through CreateRequest; the headless test asserts it
 * carries the plugin's own version.
 */
struct MANTLEPLACERUNTIME_API FMantlePlaceClientHttp
{
	/** The installed plugin's version (`VersionName` in the .uplugin, the one place it lives); empty if the plugin is not found. */
	static FString PluginVersion();

	/** The header value this install sends: FMantlePlaceClientLogic::BuildHeaderValue(PluginVersion()). */
	static FString HeaderValue();

	/** A request with the verb and URL set and the client header already on it. */
	static TSharedRef<IHttpRequest, ESPMode::ThreadSafe> CreateRequest(const FString& Verb, const FString& Url);
};
