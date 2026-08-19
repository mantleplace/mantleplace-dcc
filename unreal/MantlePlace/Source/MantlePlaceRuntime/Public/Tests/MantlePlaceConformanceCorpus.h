// Copyright Mantle Place. All Rights Reserved.

#pragma once

#include "Misc/AutomationTest.h"

#if WITH_DEV_AUTOMATION_TESTS

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

/**
 * Reader for the shared conformance corpus at `tools/manifest-conformance/corpus/`
 * (HPS-40 / HPS-41 / HPS-46).
 *
 * The corpus is the cross-host specification of manifest, vault, auth, cache, digest and
 * projection behaviour. Unreal is host #1 and seeded it, so this reader exists to stop the two
 * drifting apart: every vector a host suite asserts is read from the corpus at run time rather
 * than transcribed into C++ literals. A second host reads the same bytes.
 *
 * Two deliberate design choices:
 *
 *  - **A missing corpus is a FAILURE, never a skip.** A reader that quietly finds nothing turns
 *    HPS-40 into a no-op that reports green — the exact failure the rule exists to prevent. The
 *    corpus is checked into this repo, so "not found" means the layout moved and the suite must say so.
 *  - **A declared-but-unread expectation key is a FAILURE** (HPS-46, see UnassertedExpectations).
 *    Consumption is proven by what was actually ASSERTED: the Wants* accessors record each key
 *    they read on a successful typed read, and a key the case declares that was never recorded —
 *    unknown to the host, declared with the wrong JSON type, or on an assertion path that never
 *    ran — fails the suite. An allow-list of known key NAMES cannot express this: it catches an
 *    unknown key but not `"orderId": 999`, which asserts nothing while still counting as covered.
 *
 * The reader itself is proven by the self-test corpus at `corpus/self-test/` (HPS-46):
 * deliberately broken fixtures every host's reader must REJECT, driven through LoadGroupFromDir
 * and UnindexedCaseFiles below.
 *
 * Header-only and `WITH_DEV_AUTOMATION_TESTS`-guarded: it ships no symbols and both plugin
 * modules' test TUs can include it (`#include "Tests/MantlePlaceConformanceCorpus.h"`).
 */
namespace MantlePlaceConformanceCorpus
{
/** This host's key in a case's `appliesTo` field (HPS-41). */
inline const TCHAR* HostKey()
{
	return TEXT("unreal");
}

/** One case from index.json, with its vector file already loaded. */
struct FCase
{
	FString Id;
	FString Group;
	FString File;
	FString Expect;        // "accept" | "reject" | "vector"
	FString ErrorContains; // empty when the case does not constrain the message
	FString Reason;

	/** Raw bytes of `File` as text. The whole point for parser cases: the host feeds this to its
	 *  own parser rather than to a pre-digested structure. */
	FString Payload;

	/** `File` parsed as JSON. Null for the deliberately-malformed cases (`malformedJson`), which
	 *  is why parser cases use Payload and known-answer tables use this. */
	TSharedPtr<FJsonObject> PayloadObject;

	/** `expectations` — null when the case declares none. */
	TSharedPtr<FJsonObject> Expectations;

	/** Expectation keys this host actually READ and asserted, recorded by the Wants* accessors on
	 *  a successful typed read only. Tracked rather than assumed (HPS-46): an accessor returns
	 *  false both for "the case does not declare this" and for "it declares it with a type the
	 *  host cannot read", and the second must not pass silently — a corpus typo like
	 *  `"orderId": 999` would otherwise assert nothing while still counting as covered. Mutable
	 *  because recording a read does not change what the case IS. */
	mutable TSet<FString> AssertedKeys;

	/** Paths BELOW the top level of `expectations` this host read, recorded by the ExpectRow*
	 *  accessors (HPS-46b). AssertedKeys proves the top level and stops there: recording `items`
	 *  says nothing about the thirty-four leaves inside its two rows, so a host asserting one of
	 *  them is indistinguishable from a host asserting all of them — the blind spot one level down.
	 *  Paths are `items[1].hasManifestVersion`, the form HPS-46a set the precedent for, because
	 *  "a key in items" is not actionable. */
	mutable TSet<FString> AssertedPaths;

	bool IsAccept() const { return Expect == TEXT("accept"); }
	bool IsReject() const { return Expect == TEXT("reject"); }
	bool IsVector() const { return Expect == TEXT("vector"); }

	/** Prefix for automation-test messages, so a failure names the corpus case that caused it. */
	FString What(const TCHAR* Detail) const { return FString::Printf(TEXT("[%s] %s"), *Id, Detail); }
};

namespace Detail
{
	/** Where the walk starts: this plugin's own base directory. The corpus lives in the same repo
	 *  as the plugin (`tools/manifest-conformance/corpus` at that repo's root), so anchoring on
	 *  the plugin resolves wherever the repo is mounted — including as a submodule under a
	 *  consuming project's Plugins/ tree, where no ancestor of ProjectDir carries the corpus.
	 *  ProjectDir is the fallback for the one context with no plugin manager entry to ask. */
	inline FString CorpusSearchAnchor()
	{
		const TSharedPtr<IPlugin> ThisPlugin = IPluginManager::Get().FindPlugin(TEXT("MantlePlace"));
		return FPaths::ConvertRelativePathToFull(
			ThisPlugin.IsValid() ? ThisPlugin->GetBaseDir() : FPaths::ProjectDir());
	}

	/** Walk up from the plugin directory looking for the corpus. The walk keeps this working if
	 *  the repo's tree is ever re-rooted, without hardcoding a depth. */
	inline FString FindCorpusDir()
	{
		FString Dir = CorpusSearchAnchor();
		FPaths::NormalizeDirectoryName(Dir);

		for (int32 Up = 0; Up < 8 && !Dir.IsEmpty(); ++Up)
		{
			const FString Candidate =
				FPaths::Combine(Dir, TEXT("tools"), TEXT("manifest-conformance"), TEXT("corpus"));
			if (FPaths::FileExists(FPaths::Combine(Candidate, TEXT("index.json"))))
			{
				return Candidate;
			}

			const FString Parent = FPaths::GetPath(Dir);
			if (Parent == Dir)
			{
				break;
			}
			Dir = Parent;
		}
		return FString();
	}

	inline bool LoadJsonObject(const FString& Path, TSharedPtr<FJsonObject>& OutObject, FString& OutText)
	{
		if (!FFileHelper::LoadFileToString(OutText, *Path))
		{
			return false;
		}
		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(OutText);
		return FJsonSerializer::Deserialize(Reader, OutObject) && OutObject.IsValid();
	}

	/** Whether a key is prose for a human rather than an assertion for a host: `$comment`, or a
	 *  name ending in `Note`. The convention holds at EVERY depth (HPS-46) — a reader exempting
	 *  documentation only at the top level reports the corpus's own annotations as gaps. */
	inline bool IsDocumentationKey(const FString& Key)
	{
		return Key == TEXT("$comment") || Key.EndsWith(TEXT("Note"), ESearchCase::CaseSensitive);
	}

	/** JSON type name for failure messages, so "declared with an unexpected JSON type" can say which. */
	inline const TCHAR* JsonTypeName(EJson Type)
	{
		switch (Type)
		{
			case EJson::Null:    return TEXT("null");
			case EJson::String:  return TEXT("string");
			case EJson::Number:  return TEXT("number");
			case EJson::Boolean: return TEXT("boolean");
			case EJson::Array:   return TEXT("array");
			case EJson::Object:  return TEXT("object");
			default:             return TEXT("none");
		}
	}

	/** Whether a JSON value is a container with something inside it. */
	inline bool HasChildren(const TSharedPtr<FJsonValue>& Value)
	{
		if (!Value.IsValid())
		{
			return false;
		}
		if (Value->Type == EJson::Object)
		{
			const TSharedPtr<FJsonObject>* Object = nullptr;
			return Value->TryGetObject(Object) && Object != nullptr && (*Object)->Values.Num() > 0;
		}
		if (Value->Type == EJson::Array)
		{
			const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
			return Value->TryGetArray(Array) && Array != nullptr && Array->Num() > 0;
		}
		return false;
	}

	/** Every leaf under `Path`, with the JSON type it is declared with (HPS-46b).
	 *
	 *  Documentation prunes its whole subtree — prose is exempt along with anything under it. An
	 *  EMPTY container is itself a leaf: `formats: []` on the legacy row is the assertion "this row
	 *  has none", and a walk that found nothing under it would let the key go unread for free. An
	 *  explicit null is a leaf for the reason it is a value in a vector file — a tracker that
	 *  treated it as nothing to track would make it the one leaf a suite skips for free (⛔HPS-27).
	 */
	inline void CollectNestedLeaves(
		const TSharedPtr<FJsonValue>& Value,
		const FString& Path,
		TArray<TPair<FString, EJson>>& OutLeaves)
	{
		if (!HasChildren(Value))
		{
			OutLeaves.Emplace(Path, Value.IsValid() ? Value->Type : EJson::None);
			return;
		}

		if (Value->Type == EJson::Object)
		{
			const TSharedPtr<FJsonObject>* Object = nullptr;
			Value->TryGetObject(Object);
			for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : (*Object)->Values)
			{
				if (IsDocumentationKey(Pair.Key))
				{
					continue;
				}
				CollectNestedLeaves(Pair.Value, Path + TEXT(".") + Pair.Key, OutLeaves);
			}
			return;
		}

		const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
		Value->TryGetArray(Array);
		for (int32 Index = 0; Index < Array->Num(); ++Index)
		{
			CollectNestedLeaves((*Array)[Index], FString::Printf(TEXT("%s[%d]"), *Path, Index), OutLeaves);
		}
	}

	/** Record a strictly typed read of `Value` at `Path`, and say whether there is a value to
	 *  assert.
	 *
	 *  An explicit null records but yields nothing: in this corpus null is a VALUE (UNKNOWN), and a
	 *  tracker skipping it hands a suite one free leaf. A wrong type records NOTHING, which is what
	 *  makes "no assertion" and "mistyped" fail identically — the whole point of HPS-46.
	 */
	inline bool RecordTypedRead(
		const FCase& Case,
		const FString& Path,
		const TSharedPtr<FJsonValue>& Value,
		EJson Wanted)
	{
		if (!Value.IsValid())
		{
			return false;
		}
		if (Value->Type == EJson::Null)
		{
			Case.AssertedPaths.Add(Path);
			return false;
		}
		if (Value->Type != Wanted)
		{
			return false;
		}
		Case.AssertedPaths.Add(Path);
		return true;
	}
} // namespace Detail

/** Manifest version the corpus is pinned at (index.json `manifestVersion`); 0 if unreadable. */
inline int32 PinnedManifestVersion()
{
	const FString Root = Detail::FindCorpusDir();
	if (Root.IsEmpty())
	{
		return 0;
	}
	TSharedPtr<FJsonObject> Index;
	FString Text;
	if (!Detail::LoadJsonObject(FPaths::Combine(Root, TEXT("index.json")), Index, Text))
	{
		return 0;
	}
	double Version = 0.0;
	Index->TryGetNumberField(TEXT("manifestVersion"), Version);
	return static_cast<int32>(Version);
}

/**
 * Load every case in `Group` from the corpus rooted at `Root` — the walked-up main corpus for
 * LoadGroup below, or an explicit directory (the HPS-46 self-test corpus at `corpus/self-test/`
 * and its broken-index siblings) here.
 *
 * Structural rot in the index is collected rather than fail-fast — a case naming a missing vector
 * file, a case whose bytes do not parse WITHOUT a `malformedJson` declaration, a duplicate id —
 * and any of it returns false with every problem in OutError (HPS-46: the reader flags rot, it
 * never skips it). OutCases still receives the cases that DID load, which is what lets the reader
 * self-test drive its per-case fixtures out of a deliberately rotten index.
 */
inline bool LoadGroupFromDir(const FString& Root, const TCHAR* Group, TArray<FCase>& OutCases, FString& OutError)
{
	OutCases.Reset();
	OutError.Reset();

	TSharedPtr<FJsonObject> Index;
	FString IndexText;
	if (!Detail::LoadJsonObject(FPaths::Combine(Root, TEXT("index.json")), Index, IndexText))
	{
		OutError = FString::Printf(TEXT("corpus index.json under '%s' is unreadable or not valid JSON"), *Root);
		return false;
	}

	const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
	if (!Index->TryGetArrayField(TEXT("cases"), Cases) || Cases == nullptr)
	{
		OutError = TEXT("corpus index.json has no `cases` array");
		return false;
	}

	TArray<FString> Problems;
	TSet<FString> SeenIds;
	for (const TSharedPtr<FJsonValue>& Value : *Cases)
	{
		const TSharedPtr<FJsonObject>* Entry = nullptr;
		if (!Value.IsValid() || !Value->TryGetObject(Entry) || Entry == nullptr)
		{
			continue;
		}
		const TSharedPtr<FJsonObject>& Object = *Entry;

		FString CaseGroup;
		Object->TryGetStringField(TEXT("group"), CaseGroup);
		if (CaseGroup != Group)
		{
			continue;
		}

		// HPS-41: a case scoped to another host's manifest block is not this host's to assert.
		// Strictly typed: UE's TryGetStringField coerces a number to its string form, so a
		// non-string `appliesTo` would scope this case for Unreal and not for a host whose JSON
		// reader is strict — readers disagreeing about malformed data is the same divergence
		// HPS-46 exists to prevent. A non-string value scopes nothing here and is caught as rot by
		// the gate, which fails any `appliesTo` naming no registered host.
		const TSharedPtr<FJsonValue> AppliesToValue = Object->TryGetField(TEXT("appliesTo"));
		if (AppliesToValue.IsValid() && AppliesToValue->Type == EJson::String
			&& AppliesToValue->AsString() != HostKey())
		{
			continue;
		}

		FCase Case;
		Case.Group = CaseGroup;
		Object->TryGetStringField(TEXT("id"), Case.Id);
		Object->TryGetStringField(TEXT("file"), Case.File);
		Object->TryGetStringField(TEXT("expect"), Case.Expect);
		Object->TryGetStringField(TEXT("errorContains"), Case.ErrorContains);
		Object->TryGetStringField(TEXT("reason"), Case.Reason);

		// Two cases with one id means one of them is invisible to every id-dispatched suite.
		if (SeenIds.Contains(Case.Id))
		{
			Problems.Add(FString::Printf(
				TEXT("case id '%s' appears more than once in the index — the later entry is invisible ")
				TEXT("to id-dispatched suites (HPS-46)"),
				*Case.Id));
			continue;
		}
		SeenIds.Add(Case.Id);

		const TSharedPtr<FJsonObject>* ExpectationsPtr = nullptr;
		if (Object->TryGetObjectField(TEXT("expectations"), ExpectationsPtr) && ExpectationsPtr != nullptr)
		{
			Case.Expectations = *ExpectationsPtr;
		}

		const FString VectorPath = FPaths::Combine(Root, Case.File);
		if (!FFileHelper::LoadFileToString(Case.Payload, *VectorPath))
		{
			Problems.Add(FString::Printf(TEXT("case '%s' names a missing vector file: %s"), *Case.Id, *VectorPath));
			continue;
		}

		// Deliberately-malformed cases declare `malformedJson` in the index and leave PayloadObject
		// null — that is data. Unparseable bytes WITHOUT the declaration are corpus rot the reader
		// must surface (HPS-46), not a fixture.
		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(Case.Payload);
		TSharedPtr<FJsonObject> Parsed;
		if (FJsonSerializer::Deserialize(Reader, Parsed) && Parsed.IsValid())
		{
			Case.PayloadObject = Parsed;
		}
		else
		{
			bool bDeclaredMalformed = false;
			Object->TryGetBoolField(TEXT("malformedJson"), bDeclaredMalformed);
			if (!bDeclaredMalformed)
			{
				Problems.Add(FString::Printf(
					TEXT("case '%s' file is not valid JSON and the index does not declare malformedJson — ")
					TEXT("undeclared rot, not a deliberate fixture (HPS-46): %s"),
					*Case.Id, *VectorPath));
				continue;
			}
		}

		OutCases.Add(MoveTemp(Case));
	}

	if (Problems.Num() > 0)
	{
		OutError = FString::Join(Problems, TEXT("\n"));
		return false;
	}

	if (OutCases.Num() == 0)
	{
		OutError = FString::Printf(
			TEXT("corpus group '%s' resolved to zero cases for host '%s' — a suite that asserts nothing ")
			TEXT("reports green for the wrong reason (HPS-40)"),
			Group, HostKey());
		return false;
	}

	return true;
}

/**
 * Load every case in `Group` from the main corpus that applies to this host, with its vector file
 * read. Returns false and fills OutError if the corpus cannot be located or read, or if the group
 * is empty — an empty group means the host is asserting nothing, which must not pass silently.
 */
inline bool LoadGroup(const TCHAR* Group, TArray<FCase>& OutCases, FString& OutError)
{
	OutCases.Reset();

	const FString Root = Detail::FindCorpusDir();
	if (Root.IsEmpty())
	{
		OutError = FString::Printf(
			TEXT("could not locate tools/manifest-conformance/corpus/index.json by walking up from '%s'. ")
			TEXT("The shared corpus is checked into this repo; a working tree without it cannot assert ")
			TEXT("HPS-40 conformance."),
			*Detail::CorpusSearchAnchor());
		return false;
	}

	return LoadGroupFromDir(Root, Group, OutCases, OutError);
}

/** Absolute path of the main corpus directory (walked up from the plugin dir), or empty if not found.
 *  Exposed so the HPS-46 reader self-test can point LoadGroupFromDir at `<root>/self-test` and its
 *  broken-index siblings; ordinary group loading goes through LoadGroup. */
inline FString FindCorpusRoot()
{
	return Detail::FindCorpusDir();
}

/**
 * Case files on disk under Root that the index's `cases` never name — the HPS-46 directory sweep.
 * A vector file the index forgot is invisible to every suite while looking committed and reviewed.
 * The comparison is against ALL index entries, whatever their group or `appliesTo` — the sweep asks
 * "does the index know this file", not "does this host consume it". The index file itself is
 * skipped, as is any subdirectory carrying its own index.json (a nested corpus: `self-test` under
 * the corpus proper, the `broken-index-*` dirs under self-test). Paths in OutFiles are
 * Root-relative with '/' separators. Returns false (with OutError) only when the index cannot be
 * read.
 */
inline bool UnindexedCaseFiles(const FString& Root, TArray<FString>& OutFiles, FString& OutError)
{
	OutFiles.Reset();
	OutError.Reset();

	TSharedPtr<FJsonObject> Index;
	FString IndexText;
	if (!Detail::LoadJsonObject(FPaths::Combine(Root, TEXT("index.json")), Index, IndexText))
	{
		OutError = FString::Printf(TEXT("corpus index.json under '%s' is unreadable or not valid JSON"), *Root);
		return false;
	}

	TSet<FString> Indexed;
	const TArray<TSharedPtr<FJsonValue>>* Cases = nullptr;
	if (Index->TryGetArrayField(TEXT("cases"), Cases) && Cases != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Value : *Cases)
		{
			const TSharedPtr<FJsonObject>* Entry = nullptr;
			FString File;
			if (Value.IsValid() && Value->TryGetObject(Entry) && Entry != nullptr
				&& (*Entry)->TryGetStringField(TEXT("file"), File))
			{
				Indexed.Add(File.Replace(TEXT("\\"), TEXT("/")));
			}
		}
	}

	FString NormalRoot = FPaths::ConvertRelativePathToFull(Root);
	FPaths::NormalizeDirectoryName(NormalRoot);

	TArray<FString> OnDisk;
	IFileManager::Get().FindFilesRecursive(OnDisk, *NormalRoot, TEXT("*.json"), /*Files=*/true, /*Directories=*/false);
	for (FString& Path : OnDisk)
	{
		FPaths::NormalizeFilename(Path);
		FString Relative = Path;
		FPaths::MakePathRelativeTo(Relative, *(NormalRoot + TEXT("/")));
		if (Relative == TEXT("index.json"))
		{
			continue;
		}

		// A directory with its own index.json is a nested corpus — not this index's to sweep.
		bool bNestedCorpus = false;
		TArray<FString> Segments;
		Relative.ParseIntoArray(Segments, TEXT("/"), /*InCullEmpty=*/true);
		FString Prefix;
		for (int32 Depth = 0; Depth < Segments.Num() - 1; ++Depth)
		{
			Prefix = Prefix.IsEmpty() ? Segments[Depth] : (Prefix + TEXT("/") + Segments[Depth]);
			if (FPaths::FileExists(FPaths::Combine(NormalRoot, Prefix, TEXT("index.json"))))
			{
				bNestedCorpus = true;
				break;
			}
		}
		if (bNestedCorpus || Indexed.Contains(Relative))
		{
			continue;
		}
		OutFiles.Add(Relative);
	}
	return true;
}

//~ ----- Expectation accessors -------------------------------------------------------------
//
// Each returns false when the case does not declare that key, so a caller reads as
// "assert this if the corpus asks for it". Each records the key in the case's AssertedKeys on a
// SUCCESSFUL typed read only — a declared key with the wrong JSON type reads nothing and records
// nothing, so UnassertedExpectations flags it (HPS-46). The type checks are strict on purpose:
// UE's TryGet*Field coerces across JSON types (a number reads back as its string form), and
// coercion is exactly what HPS-46 forbids — a mistyped expectation must read as NOTHING.

inline bool WantsString(const FCase& Case, const TCHAR* Key, FString& OutValue)
{
	const TSharedPtr<FJsonValue> Value =
		Case.Expectations.IsValid() ? Case.Expectations->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::String)
	{
		return false;
	}
	OutValue = Value->AsString();
	Case.AssertedKeys.Add(Key);
	return true;
}

inline bool WantsBool(const FCase& Case, const TCHAR* Key, bool& OutValue)
{
	const TSharedPtr<FJsonValue> Value =
		Case.Expectations.IsValid() ? Case.Expectations->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Boolean)
	{
		return false;
	}
	OutValue = Value->AsBool();
	Case.AssertedKeys.Add(Key);
	return true;
}

inline bool WantsNumber(const FCase& Case, const TCHAR* Key, double& OutValue)
{
	const TSharedPtr<FJsonValue> Value =
		Case.Expectations.IsValid() ? Case.Expectations->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Number)
	{
		return false;
	}
	OutValue = Value->AsNumber();
	Case.AssertedKeys.Add(Key);
	return true;
}

inline bool WantsInt(const FCase& Case, const TCHAR* Key, int32& OutValue)
{
	double Number = 0.0;
	if (!WantsNumber(Case, Key, Number))
	{
		return false;
	}
	OutValue = static_cast<int32>(Number);
	return true;
}

/** A fixed-length numeric tuple, e.g. `landscapeScale: [x, y, z]`. False (and nothing recorded) if
 *  absent, the wrong arity, or any element is not a number. */
inline bool WantsNumbers(const FCase& Case, const TCHAR* Key, int32 ExpectedNum, TArray<double>& OutValues)
{
	OutValues.Reset();
	if (!Case.Expectations.IsValid())
	{
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (!Case.Expectations->TryGetArrayField(Key, Array) || Array == nullptr || Array->Num() != ExpectedNum)
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Array)
	{
		if (!Value.IsValid() || Value->Type != EJson::Number)
		{
			OutValues.Reset();
			return false;
		}
		OutValues.Add(Value->AsNumber());
	}
	Case.AssertedKeys.Add(Key);
	// Every element was read strictly, so every element path is asserted (HPS-46b) — a tuple read
	// whole is not a tuple half-read.
	for (int32 Index = 0; Index < Array->Num(); ++Index)
	{
		Case.AssertedPaths.Add(FString::Printf(TEXT("%s[%d]"), Key, Index));
	}
	return true;
}

/** An array of strings, e.g. `orderIds: ["ok1"]`. False (and nothing recorded) if absent or any
 *  element is not a string. */
inline bool WantsStringArray(const FCase& Case, const TCHAR* Key, TArray<FString>& OutValues)
{
	OutValues.Reset();
	if (!Case.Expectations.IsValid())
	{
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (!Case.Expectations->TryGetArrayField(Key, Array) || Array == nullptr)
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Array)
	{
		if (!Value.IsValid() || Value->Type != EJson::String)
		{
			OutValues.Reset();
			return false;
		}
		OutValues.Add(Value->AsString());
	}
	Case.AssertedKeys.Add(Key);
	for (int32 Index = 0; Index < Array->Num(); ++Index)
	{
		Case.AssertedPaths.Add(FString::Printf(TEXT("%s[%d]"), Key, Index));
	}
	return true;
}

/** An array of objects, e.g. `items: [{...}, {...}]`. False (and nothing recorded) if absent or
 *  any element is not an object. */
inline bool WantsObjectRows(const FCase& Case, const TCHAR* Key, TArray<TSharedPtr<FJsonObject>>& OutRows)
{
	OutRows.Reset();
	if (!Case.Expectations.IsValid())
	{
		return false;
	}
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (!Case.Expectations->TryGetArrayField(Key, Array) || Array == nullptr)
	{
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Array)
	{
		const TSharedPtr<FJsonObject>* Object = nullptr;
		if (!Value.IsValid() || !Value->TryGetObject(Object) || Object == nullptr)
		{
			OutRows.Reset();
			return false;
		}
		OutRows.Add(*Object);
	}
	Case.AssertedKeys.Add(Key);
	return true;
}

/** A tolerance the case states, or Fallback when it states none. Never tighten a stated one. */
inline double ToleranceOr(const FCase& Case, const TCHAR* Key, double Fallback)
{
	double Value = Fallback;
	WantsNumber(Case, Key, Value);
	return Value;
}

//~ ----- Nested expectation accessors (HPS-46b) -----------------------------------------------
//
// Everything BELOW the top level of `expectations`. Each takes the owning row's path, so a failure
// names `items[1].hasManifestVersion` rather than "a key in items" — `HPS-46a` set that precedent
// because "something in this file" is not actionable. Each records only on a successful STRICTLY
// typed read.
//
// UE's TryGet*Field is deliberately absent from this family. It coerces across JSON types, so
// `"status": 404` reads back as "404", asserts happily and marks the path read — the asserted-keys
// bug one
// level down, and coercion is precisely what this rule forbids. These replace the allow-list check
// that used to guard row keys: an allow-list says "this host knows the key `status`" and cannot
// notice that this row spelled it as a number and asserted nothing.

/** A child path under `RowPath`, or a bare key at the top level. */
inline FString ExpectPath(const FString& RowPath, const TCHAR* Key)
{
	return RowPath.IsEmpty() ? FString(Key) : FString::Printf(TEXT("%s.%s"), *RowPath, Key);
}

inline bool ExpectRowString(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, FString& OutValue)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Detail::RecordTypedRead(Case, ExpectPath(RowPath, Key), Value, EJson::String))
	{
		return false;
	}
	OutValue = Value->AsString();
	return true;
}

inline bool ExpectRowBool(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, bool& bOutValue)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Detail::RecordTypedRead(Case, ExpectPath(RowPath, Key), Value, EJson::Boolean))
	{
		return false;
	}
	bOutValue = Value->AsBool();
	return true;
}

inline bool ExpectRowNumber(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, double& OutValue)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Detail::RecordTypedRead(Case, ExpectPath(RowPath, Key), Value, EJson::Number))
	{
		return false;
	}
	OutValue = Value->AsNumber();
	return true;
}

/** An object-valued row key. An EMPTY object records here rather than through a child read,
 *  because it has no children — "known to hold nothing" is still an assertion. */
inline bool ExpectRowObject(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, TSharedPtr<FJsonObject>& OutObject)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Object)
	{
		return false;
	}
	const TSharedPtr<FJsonObject>* Object = nullptr;
	if (!Value->TryGetObject(Object) || Object == nullptr)
	{
		return false;
	}
	if (!Detail::HasChildren(Value))
	{
		Case.AssertedPaths.Add(ExpectPath(RowPath, Key));
	}
	OutObject = *Object;
	return true;
}

/** An array-valued row key. An EMPTY array records its own path, for the reason ExpectRowObject
 *  gives: `formats: []` on the legacy row is the assertion that the row has none. */
inline bool ExpectRowArray(
	const FCase& Case, const FString& RowPath, const TSharedPtr<FJsonObject>& Row,
	const TCHAR* Key, const TArray<TSharedPtr<FJsonValue>>*& OutArray)
{
	const TSharedPtr<FJsonValue> Value = Row.IsValid() ? Row->TryGetField(Key) : nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Array)
	{
		return false;
	}
	if (!Value->TryGetArray(OutArray) || OutArray == nullptr)
	{
		return false;
	}
	if (!Detail::HasChildren(Value))
	{
		Case.AssertedPaths.Add(ExpectPath(RowPath, Key));
	}
	return true;
}

/** One element of a string array, e.g. `items[0].formats[1]`. */
inline bool ExpectElementString(
	const FCase& Case, const FString& ElementPath, const TSharedPtr<FJsonValue>& Value,
	FString& OutValue)
{
	if (!Detail::RecordTypedRead(Case, ElementPath, Value, EJson::String))
	{
		return false;
	}
	OutValue = Value->AsString();
	return true;
}

/** One element of a numeric array, e.g. `landscapeLayers[7].ueReady[0].mapping.classes[2]`. */
inline bool ExpectElementNumber(
	const FCase& Case, const FString& ElementPath, const TSharedPtr<FJsonValue>& Value,
	double& OutValue)
{
	if (!Detail::RecordTypedRead(Case, ElementPath, Value, EJson::Number))
	{
		return false;
	}
	OutValue = Value->AsNumber();
	return true;
}

/** One element of an object array, e.g. `items[0].downloadFormats[1]`. Its own keys record
 *  themselves against ElementPath, so nothing is recorded here. */
inline TSharedPtr<FJsonObject> ExpectElementObject(const TSharedPtr<FJsonValue>& Value)
{
	const TSharedPtr<FJsonObject>* Object = nullptr;
	if (!Value.IsValid() || Value->Type != EJson::Object || !Value->TryGetObject(Object) || Object == nullptr)
	{
		return nullptr;
	}
	return *Object;
}

/** Every leaf below the top level of `expectations`, with its declared type. */
inline TArray<TPair<FString, EJson>> NestedExpectationLeaves(const FCase& Case)
{
	TArray<TPair<FString, EJson>> Leaves;
	if (!Case.Expectations.IsValid())
	{
		return Leaves;
	}
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : Case.Expectations->Values)
	{
		// The top level is HPS-46's. Only a container with something in it contributes here — an
		// empty top-level container is a key, and the key is already bound one rule up.
		if (!Detail::IsDocumentationKey(Pair.Key) && Detail::HasChildren(Pair.Value))
		{
			Detail::CollectNestedLeaves(Pair.Value, Pair.Key, Leaves);
		}
	}
	Leaves.Sort([](const TPair<FString, EJson>& Left, const TPair<FString, EJson>& Right)
	{
		return Left.Key < Right.Key;
	});
	return Leaves;
}

/**
 * Nested expectation paths this host never read — a non-empty result is a failure (HPS-46b).
 *
 * One message, because "not read" is one failure: nothing asserts the path, or it is declared with
 * a type the accessor rejects, or the assertion path never ran. The declared JSON type rides along
 * so the mistyped cause stays diagnosable without becoming a different failure.
 */
inline TArray<FString> UnassertedNestedExpectations(const FCase& Case)
{
	TArray<FString> Problems;
	for (const TPair<FString, EJson>& Leaf : NestedExpectationLeaves(Case))
	{
		if (Case.AssertedPaths.Contains(Leaf.Key))
		{
			continue;
		}
		Problems.Add(FString::Printf(
			TEXT("'%s' is a nested expectation this host never read (%s). Nothing asserts it, or it ")
			TEXT("is declared with a type the accessor rejects (HPS-46b) — teach the suite to ")
			TEXT("consume it, do not delete it from the corpus"),
			*Leaf.Key,
			Detail::JsonTypeName(Leaf.Value)));
	}
	return Problems;
}

/**
 * Every `expectations` key the case declares that this host did not end up ASSERTING, with the
 * reason — a non-empty result is a failure (HPS-46): either the corpus asks for an assertion this
 * host does not make, or it declares one with a type the host could not read.
 *
 * Checking what was ASSERTED rather than what is merely in an allow-list is what catches the
 * second case. The allow-list says "this host knows the key `orderId`" — it cannot notice that
 * this particular case spelled the value as a number and so asserted nothing at all. Consumed is
 * kept solely to tell the two failure messages apart.
 */
inline TArray<FString> UnassertedExpectations(const FCase& Case, TConstArrayView<const TCHAR*> Consumed)
{
	TArray<FString> Problems;
	if (!Case.Expectations.IsValid())
	{
		return Problems;
	}
	for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : Case.Expectations->Values)
	{
		if (Case.AssertedKeys.Contains(Pair.Key))
		{
			continue;
		}

		bool bKnown = false;
		for (const TCHAR* Known : Consumed)
		{
			if (Pair.Key == Known)
			{
				bKnown = true;
				break;
			}
		}

		Problems.Add(bKnown
			? FString::Printf(
				TEXT("'%s' is declared with an unexpected JSON type (%s), so this host read nothing ")
				TEXT("from it (HPS-46)"),
				*Pair.Key,
				Detail::JsonTypeName(Pair.Value.IsValid() ? Pair.Value->Type : EJson::None))
			: FString::Printf(
				TEXT("'%s' is an expectation this host does not assert. Teach the suite to consume it ")
				TEXT("(HPS-46) — do not delete it from the corpus"),
				*Pair.Key));
	}
	return Problems;
}

/** A known-answer table's rows: `PayloadObject[ArrayField]` as objects. Empty if absent. */
inline TArray<TSharedPtr<FJsonObject>> Rows(const FCase& Case, const TCHAR* ArrayField)
{
	TArray<TSharedPtr<FJsonObject>> Out;
	if (!Case.PayloadObject.IsValid())
	{
		return Out;
	}
	const TArray<TSharedPtr<FJsonValue>>* Array = nullptr;
	if (!Case.PayloadObject->TryGetArrayField(ArrayField, Array) || Array == nullptr)
	{
		return Out;
	}
	for (const TSharedPtr<FJsonValue>& Value : *Array)
	{
		const TSharedPtr<FJsonObject>* Object = nullptr;
		if (Value.IsValid() && Value->TryGetObject(Object) && Object != nullptr)
		{
			Out.Add(*Object);
		}
	}
	return Out;
}

/**
 * Ids in Cases that Driven does not contain — the coverage half of HPS-41. Groups whose cases each
 * exercise a different parser are driven by id rather than in a loop, and without this a case added
 * to the corpus would be silently ignored by a suite that still reports green.
 */
inline TArray<FString> UndrivenCases(const TArray<FCase>& Cases, const TSet<FString>& Driven)
{
	TArray<FString> Missing;
	for (const FCase& Case : Cases)
	{
		if (!Driven.Contains(Case.Id))
		{
			Missing.Add(Case.Id);
		}
	}
	return Missing;
}

/** Find one case by id. Null if the group does not contain it (which callers should assert on). */
inline const FCase* FindCase(const TArray<FCase>& Cases, const TCHAR* Id)
{
	return Cases.FindByPredicate([Id](const FCase& Case) { return Case.Id == Id; });
}

//~ ----- Small JSON row helpers, so the per-group drivers stay readable -----------------------

inline FString RowString(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key)
{
	FString Value;
	if (Row.IsValid())
	{
		Row->TryGetStringField(Key, Value);
	}
	return Value;
}

inline double RowNumber(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key, double Fallback = 0.0)
{
	double Value = Fallback;
	if (Row.IsValid())
	{
		Row->TryGetNumberField(Key, Value);
	}
	return Value;
}

inline bool RowBool(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key, bool bFallback = false)
{
	bool bValue = bFallback;
	if (Row.IsValid())
	{
		Row->TryGetBoolField(Key, bValue);
	}
	return bValue;
}

inline TArray<FString> RowStrings(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key)
{
	TArray<FString> Out;
	if (Row.IsValid())
	{
		Row->TryGetStringArrayField(Key, Out);
	}
	return Out;
}

/** Serialize a row's `body` back to JSON text — the corpus stores request/response bodies as real
 *  JSON (readable, and checkable by the offline gate) but a parser under test wants the text. */
inline FString RowBodyAsText(const TSharedPtr<FJsonObject>& Row, const TCHAR* Key = TEXT("body"))
{
	if (!Row.IsValid())
	{
		return FString();
	}

	// `raw: true` means `body` is a literal string to feed through unchanged (e.g. "not json").
	if (RowBool(Row, TEXT("raw")))
	{
		return RowString(Row, Key);
	}

	const TSharedPtr<FJsonObject>* Body = nullptr;
	if (!Row->TryGetObjectField(Key, Body) || Body == nullptr)
	{
		return FString();
	}

	FString Text;
	const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Text);
	FJsonSerializer::Serialize(Body->ToSharedRef(), Writer);
	return Text;
}

} // namespace MantlePlaceConformanceCorpus

#endif // WITH_DEV_AUTOMATION_TESTS
