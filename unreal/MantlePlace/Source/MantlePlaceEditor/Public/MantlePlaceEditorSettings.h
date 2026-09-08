// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Engine/DeveloperSettings.h"

#include "MantlePlaceEditorSettings.generated.h"

/**
 * The plugin's project settings, under Project Settings -> Plugins -> Mantle Place.
 *
 * `config = Game` and `defaultconfig`, so a value is written to the project's DefaultGame.ini and
 * checked in — a decision the whole team shares. Deliberately NOT per-user: two developers with
 * different content roots in one project would each import the same order to a different place, and
 * neither one's re-import would ever clean up the other's copy. That is the exact failure the
 * order-keyed identity exists to prevent, so putting the root in per-user config would reintroduce
 * it through the back door.
 *
 * The knobs here are of two kinds, and the distinction is what keeps this page from becoming a
 * junk drawer.
 *
 * **Layout policy**, of which there is exactly one: the generated content root. A studio's
 * asset-layout policy is the thing that genuinely collides with a fixed `/Game/MantlePlace`, and it
 * is about package paths. Per-asset name templates are still not offered and will not be: they
 * would make every bug report begin with "what is your template?" and make the naming gate unable
 * to say what conforming looks like.
 *
 * **Numbers the plugin would otherwise hardcode** — a loopback port range, how long to wait for the
 * platform to materialize a bundle. These are not policy about what an import produces; they are
 * facts about the machine and the network it runs on, and a hardcoded one is a value nobody can
 * change when their environment disagrees with it. Every one of them defaults to exactly what the
 * code used to hardcode, so an unconfigured project behaves as it always did, and every one falls
 * back to that default when the configured value is unusable rather than failing the operation —
 * the same rule the content root follows, for the same reason.
 */
UCLASS(config = Game, defaultconfig, meta = (DisplayName = "Mantle Place"))
class MANTLEPLACEEDITOR_API UMantlePlaceEditorSettings : public UDeveloperSettings
{
	GENERATED_BODY()

public:
	UMantlePlaceEditorSettings();

	/**
	 * Where imported bundle content is written. One folder per order is created beneath this.
	 *
	 * Must be a mount point plus at least one segment (`/Game/MantlePlace`, `/Game/Studio/Geo`).
	 * An unusable value falls back to the default rather than failing the import — the importer
	 * creates and force-deletes a directory beneath this, and a malformed root is not a thing to
	 * resolve creatively.
	 */
	UPROPERTY(EditAnywhere, config, Category = "Import",
		meta = (DisplayName = "Generated content root"))
	FString GeneratedContentRoot;

	/** The configured root if it is usable, the default otherwise. What the importer calls. */
	static FString ResolveGeneratedContentRoot();

	// --- Streaming into Cesium ------------------------------------------------------------------

	/**
	 * First loopback port the bundle stream server tries.
	 *
	 * It scans upward from here rather than binding this one and giving up, because a busy port is a
	 * local condition — another editor, another tool — and a stream that fails on it has nothing
	 * useful to tell the user. The scan is the fallback, not the configuration: this is the port to
	 * change when a whole class of machine has something else on 8088.
	 */
	UPROPERTY(EditAnywhere, config, Category = "Streaming",
		meta = (DisplayName = "Local tile server first port", ClampMin = "1024", ClampMax = "65535"))
	int32 LocalTileServerFirstPort;

	/** How many consecutive ports to try, starting at the first, before giving up. */
	UPROPERTY(EditAnywhere, config, Category = "Streaming",
		meta = (DisplayName = "Local tile server port scan count", ClampMin = "1", ClampMax = "64"))
	int32 LocalTileServerPortScanCount;

	// --- Waiting for the platform to materialize a bundle ----------------------------------------

	/** Seconds between materialize status polls. */
	UPROPERTY(EditAnywhere, config, Category = "Vault",
		meta = (DisplayName = "Materialize poll interval (seconds)", ClampMin = "0.5", ClampMax = "600"))
	float MaterializePollIntervalSeconds;

	/** Give up after this many polls — interval x this is the effective materialize timeout. */
	UPROPERTY(EditAnywhere, config, Category = "Vault",
		meta = (DisplayName = "Materialize maximum polls", ClampMin = "1", ClampMax = "100000"))
	int32 MaterializeMaxPolls;

	/** Tolerate this many consecutive failed status polls (transient network) before failing. */
	UPROPERTY(EditAnywhere, config, Category = "Vault",
		meta = (DisplayName = "Tolerated consecutive poll failures", ClampMin = "0", ClampMax = "1000"))
	int32 MaxConsecutivePollFailures;

	// --- The defaults, named once ----------------------------------------------------------------
	//
	// Each is exactly the literal the code used to hardcode. Named here rather than repeated at the
	// constructor, the fallback and the call site, because three spellings of one number is how two
	// of them end up disagreeing.

	static constexpr int32 DefaultLocalTileServerFirstPort = 8088;
	static constexpr int32 DefaultLocalTileServerPortScanCount = 8;
	static constexpr float DefaultMaterializePollIntervalSeconds = 3.0f;
	static constexpr int32 DefaultMaterializeMaxPolls = 200;
	static constexpr int32 DefaultMaxConsecutivePollFailures = 5;

	// --- Usability, as pure functions --------------------------------------------------------------
	//
	// Separated from the reads below so they can be asserted headlessly. The clamps in the meta
	// specifiers above only constrain the Project Settings UI; a value edited into DefaultGame.ini by
	// hand, or carried over from an older plugin version, arrives unchecked. Each of these answers
	// "is this usable, and what happens if not" once, where a test can ask it.

	/** A port in the unprivileged range, or the default. */
	static int32 UsableLocalTileServerFirstPort(int32 Configured);

	/** A scan count of at least one that cannot run past port 65535, or the default. */
	static int32 UsableLocalTileServerPortScanCount(int32 FirstPort, int32 Configured);

	/** A poll interval of at least half a second, or the default. */
	static float UsableMaterializePollIntervalSeconds(float Configured);

	/** A positive poll budget, or the default. */
	static int32 UsableMaterializeMaxPolls(int32 Configured);

	/** A non-negative failure tolerance, or the default. */
	static int32 UsableMaxConsecutivePollFailures(int32 Configured);

	// --- The configured values if usable, the defaults otherwise. What callers call. ---------------

	static void ResolveLocalTileServerPortScan(int32& OutFirstPort, int32& OutPortCount);
	static float ResolveMaterializePollIntervalSeconds();
	static int32 ResolveMaterializeMaxPolls();
	static int32 ResolveMaxConsecutivePollFailures();

	//~ Begin UDeveloperSettings interface
	virtual FName GetCategoryName() const override;
	//~ End UDeveloperSettings interface
};
