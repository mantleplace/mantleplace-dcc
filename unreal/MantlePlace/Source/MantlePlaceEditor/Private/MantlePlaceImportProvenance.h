// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

/**
 * What the importer remembers about a folder it created, so that re-importing can tell whether that
 * folder is ITS folder before it force-deletes the contents.
 *
 * Why a record is needed at all: the identity in a folder name is truncated to eight characters for
 * readability, so two identities can name the same folder. Without a record, a second order whose
 * identity happens to share those eight characters would silently force-delete the first order's
 * imported content. The record turns that from a silent deletion into a refusal.
 *
 * Why it lives beside the project's Saved data rather than inside the content folder as an asset:
 * the record has to be readable at the moment the importer is DECIDING whether to delete that
 * folder. An asset inside the folder would have to be loaded to be read, and loading a package the
 * importer is about to force-delete is the exact shape of the hazard already documented at the
 * delete site — force-deleting an asset whose only reference is the transaction buffer purges the
 * buffer. A record outside the content is readable without touching the content.
 *
 * The consequence is honest and worth knowing: a user who deletes the content folder by hand leaves
 * a stale record behind. Classify() treats a record with no content as no prior import, which is
 * what that user meant.
 */
namespace MantlePlaceImportProvenance
{
	/**
	 * The naming scheme a folder was written by. Bumped when the layout or the identity changes
	 * meaning, so a later build can tell "written by a scheme I know" from "written by a newer one".
	 *
	 * 1 = order-keyed identity, subfolders per modality, outliner folder, tag-based re-import.
	 * Content from 0.3.0 and earlier has NO record at all, which is how it is recognised.
	 */
	constexpr int32 CurrentSchemeVersion = 1;

	struct FRecord
	{
		/** The FULL identity, untruncated — the field the collision check compares. */
		FString Identity;

		/** The scheme that wrote the folder. */
		int32 SchemeVersion = 0;

		/** The content root in force when it was written, so a changed setting is explicable. */
		FString ContentRoot;

		/** The job that produced the imported bundle. Provenance only; never keyed on. */
		FString JobId;
	};

	/** What an existing folder means for the import about to happen. */
	enum class EVerdict : uint8
	{
		/** Nothing there, or nothing that survived. Import freely. */
		NoPriorImport,

		/** Our folder, our identity. Replace the contents, which is what re-import means. */
		SameIdentity,

		/** Same truncated folder name, DIFFERENT identity. Refuse; deleting would lose their content. */
		DifferentIdentity,

		/** Content with no record — 0.3.0 or earlier. Refuse, and tell the user where it is. */
		LegacyUnrecorded,

		/** A record from a scheme this build does not know. Refuse rather than guess at its layout. */
		NewerScheme,
	};

	/**
	 * The whole decision, as a pure function of what was found. Separated from the filesystem so it
	 * can be asserted headlessly — this is the function whose wrong answer deletes a user's content.
	 */
	EVerdict Classify(
		bool bContentFolderExists,
		bool bRecordFound,
		const FRecord& Found,
		const FString& IncomingIdentity);

	/** A sentence for the import log explaining a refusal. Empty for the two verdicts that proceed. */
	FString ExplainRefusal(EVerdict Verdict, const FRecord& Found, const FString& ContentPath);

	// --- Serialization (pure) -------------------------------------------------------------------

	FString ToJson(const FRecord& Record);
	bool FromJson(const FString& Json, FRecord& OutRecord);

	// --- Storage --------------------------------------------------------------------------------

	/** Where the record for an identity's folder lives on disk. */
	FString RecordPath(const FString& Identity);

	bool Read(const FString& Identity, FRecord& OutRecord);
	bool Write(const FRecord& Record);
}
