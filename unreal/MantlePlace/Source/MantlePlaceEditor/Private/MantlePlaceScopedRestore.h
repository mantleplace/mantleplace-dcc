// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Templates/Function.h"

/**
 * Run one restore action exactly once, on whichever path leaves the scope.
 *
 * This exists because of a specific shape of bug rather than as a general convenience. The importer
 * changes editor state that outlives it -- an editor-wide setting override, a console variable --
 * and then has to put it back. Written as a `Set(...)` at the top and a `Reset(...)` before the
 * return, the pairing is correct only for the exit paths that existed when it was written: the next
 * patch to add an early return between them leaks the change into the rest of the editor session,
 * silently, with nothing to notice it. `ImportVaultPackage` is over five hundred lines and returns
 * from several of them, so "the next patch" is not hypothetical.
 *
 * Deliberately a named object rather than `ON_SCOPE_EXIT`. The macro produces an unnamed local that
 * no test can hold, and "the restore runs on every exit path" is the one property here worth
 * asserting; MantlePlace.Import.ScopedRestore asserts it, headlessly, including the early-return
 * case that motivated this.
 *
 * Not thread-safe and not meant to be: it restores editor state on the thread that changed it.
 */
class FMantlePlaceScopedRestore
{
public:
	explicit FMantlePlaceScopedRestore(TFunction<void()> InRestore)
		: Restore(MoveTemp(InRestore))
	{
	}

	~FMantlePlaceScopedRestore()
	{
		if (Restore)
		{
			Restore();
			// Cleared as well as called: a guard is destroyed once, but clearing makes "exactly
			// once" a property of the object rather than of how it happens to be used.
			Restore.Reset();
		}
	}

	// A copied guard would restore twice; a moved one would need a disarmed source. Neither is
	// wanted at a call site that reads as "put this back when the scope ends".
	FMantlePlaceScopedRestore(const FMantlePlaceScopedRestore&) = delete;
	FMantlePlaceScopedRestore& operator=(const FMantlePlaceScopedRestore&) = delete;
	FMantlePlaceScopedRestore(FMantlePlaceScopedRestore&&) = delete;
	FMantlePlaceScopedRestore& operator=(FMantlePlaceScopedRestore&&) = delete;

	/**
	 * Abandon the restore, so leaving the scope changes nothing.
	 *
	 * For the case where the scope hands the changed state on to something that outlives it -- the
	 * caller has taken ownership, so putting it back here would undo what it was given.
	 */
	void Disarm()
	{
		Restore.Reset();
	}

	/** Whether the restore is still pending. Exists for the test; cheap enough to keep honest. */
	bool IsArmed() const
	{
		return static_cast<bool>(Restore);
	}

private:
	TFunction<void()> Restore;
};
