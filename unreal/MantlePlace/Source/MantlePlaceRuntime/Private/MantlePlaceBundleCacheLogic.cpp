// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceBundleCacheLogic.h"

#include "Containers/StringConv.h" // StringConv::Is{High,Low}Surrogate / EncodeSurrogate
#include "Dom/JsonObject.h"
#include "Misc/DateTime.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonWriter.h"
#include "Serialization/JsonSerializer.h"

namespace
{
	const TCHAR* const BundleFileName = TEXT("bundle.zip");
	const TCHAR* const PartFileName = TEXT("bundle.zip.part");
	const TCHAR* const MetaFileName = TEXT("cache.json");

	FString BundleCacheSerializeCondensed(const TSharedRef<FJsonObject>& Root)
	{
		FString Out;
		const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
			TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Out);
		FJsonSerializer::Serialize(Root, Writer);
		return Out;
	}

	TSharedPtr<FJsonObject> BundleCacheDeserializeObject(const FString& JsonStr)
	{
		TSharedPtr<FJsonObject> Root;
		const TSharedRef<TJsonReader<TCHAR>> Reader = TJsonReaderFactory<TCHAR>::Create(JsonStr);
		if (FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid())
		{
			return Root;
		}
		return nullptr;
	}
}

FString FMantlePlaceBundleCacheLogic::SanitizeKeySegment(const FString& OrderId)
{
	FString Out;
	Out.Reserve(OrderId.Len()); // one code point in, at most one character out - never grows
	const int32 Length = OrderId.Len();
	for (int32 Index = 0; Index < Length; ++Index)
	{
		// Walk code POINTS. A surrogate pair is one character spread over two UTF-16 code units,
		// and consuming it as two emitted two underscores where Revit emits one - same order id,
		// two cache directories, re-downloaded on whichever host did not create it.
		// Where TCHAR is already wide enough to hold a whole code point this branch never fires
		// and the classification below still lands in the right place.
		uint32 CodePoint = static_cast<uint32>(OrderId[Index]);
		if (StringConv::IsHighSurrogate(CodePoint) && Index + 1 < Length
			&& StringConv::IsLowSurrogate(static_cast<uint32>(OrderId[Index + 1])))
		{
			CodePoint = StringConv::EncodeSurrogate(
				static_cast<uint16>(CodePoint), static_cast<uint16>(OrderId[Index + 1]));
			++Index;
		}

		// HPS-30 bounds "alphanumeric" to the BMP. Above U+FFFF the answer is "not alphanumeric"
		// on every host - which needs no Unicode table anywhere, where "keep U+1D7CE because it
		// is category Nd" would need one here and one in every host after this. An UNPAIRED
		// surrogate is not a code point at all and takes the same path.
		const bool bIsBmpCodePoint = CodePoint <= 0xFFFF
			&& !StringConv::IsHighSurrogate(CodePoint) && !StringConv::IsLowSurrogate(CodePoint);
		const TCHAR C = static_cast<TCHAR>(CodePoint);
		const bool bKeep = bIsBmpCodePoint
			&& (FChar::IsAlnum(C) || C == TEXT('.') || C == TEXT('_') || C == TEXT('-'));
		Out.AppendChar(bKeep ? C : TEXT('_'));
	}
	// A bare "." / ".." would be a directory-traversal segment; neutralize it.
	if (Out.IsEmpty() || Out == TEXT(".") || Out == TEXT(".."))
	{
		Out = TEXT("_");
	}
	return Out;
}

FString FMantlePlaceBundleCacheLogic::DeriveBundleDir(const FString& CacheRoot, const FString& OrderId)
{
	const FString Sanitized = SanitizeKeySegment(OrderId);
	// When sanitization was lossy, two distinct order ids could collapse to the same segment
	// (e.g. "a/b" and "a:b" both -> "a_b"). Suffix a short hash of the RAW id to keep their caches
	// distinct. Lossless ids (UUIDs) are left untouched so existing caches keep their clean paths.
	if (Sanitized != OrderId)
	{
		const FTCHARToUTF8 Utf8(*OrderId);
		const FString Hash = Sha256Hex(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
		return FPaths::Combine(CacheRoot, Sanitized + TEXT("_") + Hash.Left(8));
	}
	return FPaths::Combine(CacheRoot, Sanitized);
}

FString FMantlePlaceBundleCacheLogic::DeriveBundlePath(const FString& CacheRoot, const FString& OrderId)
{
	return FPaths::Combine(DeriveBundleDir(CacheRoot, OrderId), BundleFileName);
}

FString FMantlePlaceBundleCacheLogic::DerivePartPath(const FString& CacheRoot, const FString& OrderId)
{
	return FPaths::Combine(DeriveBundleDir(CacheRoot, OrderId), PartFileName);
}

FString FMantlePlaceBundleCacheLogic::DeriveMetaPath(const FString& CacheRoot, const FString& OrderId)
{
	return FPaths::Combine(DeriveBundleDir(CacheRoot, OrderId), MetaFileName);
}

FMantlePlaceCacheValidity FMantlePlaceBundleCacheLogic::DecideValidity(
	bool bFileExists,
	int64 OnDiskSizeBytes,
	const FString& ComputedSha256,
	bool bHasExpectedSha,
	const FString& ExpectedSha256,
	bool bHasExpectedSize,
	int64 ExpectedSizeBytes,
	bool bHasManifestVersion,
	int32 ManifestVersion,
	int32 MinVersion)
{
	FMantlePlaceCacheValidity Validity;

	if (!bFileExists)
	{
		Validity.bValid = false;
		Validity.Reason = EMantlePlaceCacheInvalidReason::Missing;
		Validity.bIntegrityChecked = false;
		return Validity;
	}

	// Size is the cheapest discriminator - check it first when the vault advertised one.
	if (bHasExpectedSize && OnDiskSizeBytes != ExpectedSizeBytes)
	{
		Validity.bValid = false;
		Validity.Reason = EMantlePlaceCacheInvalidReason::SizeMismatch;
		Validity.bIntegrityChecked = false;
		return Validity;
	}

	// Integrity: only when BOTH a hash was advertised AND we actually computed one (a legacy
	// bundle or an over-cap file leaves ComputedSha256 empty -> integrity simply not checked).
	const bool bDidHash = bHasExpectedSha && !ComputedSha256.IsEmpty();
	if (bDidHash && !Sha256Equal(ComputedSha256, ExpectedSha256))
	{
		Validity.bValid = false;
		Validity.Reason = EMantlePlaceCacheInvalidReason::Sha256Mismatch;
		Validity.bIntegrityChecked = true;
		return Validity;
	}

	if (bHasManifestVersion && ManifestVersion < MinVersion)
	{
		Validity.bValid = false;
		Validity.Reason = EMantlePlaceCacheInvalidReason::ManifestTooOld;
		Validity.bIntegrityChecked = bDidHash;
		return Validity;
	}

	// Valid. "Verified" only if we actually compared a hash; otherwise valid-but-unverified.
	Validity.bValid = true;
	Validity.Reason = EMantlePlaceCacheInvalidReason::None;
	Validity.bIntegrityChecked = bDidHash;
	return Validity;
}

EMantlePlaceCacheState FMantlePlaceBundleCacheLogic::DeriveCacheState(
	bool bFileExists, const FMantlePlaceCacheValidity& Validity)
{
	if (!bFileExists)
	{
		return EMantlePlaceCacheState::NotCached;
	}
	return Validity.bValid ? EMantlePlaceCacheState::CachedValid : EMantlePlaceCacheState::CachedStale;
}

bool FMantlePlaceBundleCacheLogic::ParseExpiry(const FString& Iso8601, FDateTime& OutUtc)
{
	if (Iso8601.IsEmpty())
	{
		return false;
	}
	return FDateTime::ParseIso8601(*Iso8601, OutUtc);
}

bool FMantlePlaceBundleCacheLogic::IsExpired(const FDateTime& NowUtc, const FDateTime& ExpiresAtUtc, int32 SkewSeconds)
{
	// Treat as expired once we are within SkewSeconds of (or past) the expiry.
	return (NowUtc + FTimespan::FromSeconds(SkewSeconds)) >= ExpiresAtUtc;
}

bool FMantlePlaceBundleCacheLogic::Sha256Equal(const FString& A, const FString& B)
{
	return A.TrimStartAndEnd().Equals(B.TrimStartAndEnd(), ESearchCase::IgnoreCase);
}

namespace
{
	// Standard FIPS 180-4 SHA-256 single-block compression. Self-contained because the engine's
	// GetSHA256Signature is an unimplemented stub here; correctness is pinned by the known-answer
	// tests (sha256("") / "abc" / the 56-byte NIST vector) plus the streaming-equivalence test.
	const uint32 Sha256K[64] = {
		0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u, 0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
		0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u, 0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
		0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu, 0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
		0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u, 0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
		0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u, 0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
		0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u, 0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
		0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u, 0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
		0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u, 0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u
	};

	void Sha256ProcessBlock(uint32 H[8], const uint8* P)
	{
		auto Ror = [](uint32 X, uint32 N) -> uint32 { return (X >> N) | (X << (32 - N)); };

		uint32 W[64];
		for (int32 I = 0; I < 16; ++I)
		{
			W[I] = (static_cast<uint32>(P[I * 4]) << 24) | (static_cast<uint32>(P[I * 4 + 1]) << 16)
				| (static_cast<uint32>(P[I * 4 + 2]) << 8) | static_cast<uint32>(P[I * 4 + 3]);
		}
		for (int32 I = 16; I < 64; ++I)
		{
			const uint32 S0 = Ror(W[I - 15], 7) ^ Ror(W[I - 15], 18) ^ (W[I - 15] >> 3);
			const uint32 S1 = Ror(W[I - 2], 17) ^ Ror(W[I - 2], 19) ^ (W[I - 2] >> 10);
			W[I] = W[I - 16] + S0 + W[I - 7] + S1;
		}

		uint32 A = H[0], B = H[1], C = H[2], D = H[3], E = H[4], F = H[5], G = H[6], Hh = H[7];
		for (int32 I = 0; I < 64; ++I)
		{
			const uint32 T1 = Hh + (Ror(E, 6) ^ Ror(E, 11) ^ Ror(E, 25)) + ((E & F) ^ (~E & G)) + Sha256K[I] + W[I];
			const uint32 T2 = (Ror(A, 2) ^ Ror(A, 13) ^ Ror(A, 22)) + ((A & B) ^ (A & C) ^ (B & C));
			Hh = G; G = F; F = E; E = D + T1; D = C; C = B; B = A; A = T1 + T2;
		}
		H[0] += A; H[1] += B; H[2] += C; H[3] += D; H[4] += E; H[5] += F; H[6] += G; H[7] += Hh;
	}
}

FMantlePlaceSha256::FMantlePlaceSha256()
{
	H[0] = 0x6a09e667u; H[1] = 0xbb67ae85u; H[2] = 0x3c6ef372u; H[3] = 0xa54ff53au;
	H[4] = 0x510e527fu; H[5] = 0x9b05688cu; H[6] = 0x1f83d9abu; H[7] = 0x5be0cd19u;
}

void FMantlePlaceSha256::Update(const uint8* Data, int64 NumBytes)
{
	if (Data == nullptr || NumBytes <= 0)
	{
		return;
	}
	TotalBytes += static_cast<uint64>(NumBytes);

	int64 Offset = 0;
	// Top up a partial block carried over from a previous Update.
	if (PendingLen > 0)
	{
		const int32 Take = static_cast<int32>(FMath::Min<int64>(64 - PendingLen, NumBytes));
		FMemory::Memcpy(Pending + PendingLen, Data, Take);
		PendingLen += Take;
		Offset += Take;
		if (PendingLen == 64)
		{
			Sha256ProcessBlock(H, Pending);
			PendingLen = 0;
		}
	}
	// Process whole blocks straight from the caller's buffer (no copy).
	while (NumBytes - Offset >= 64)
	{
		Sha256ProcessBlock(H, Data + Offset);
		Offset += 64;
	}
	// Buffer the trailing partial block for next time / Final.
	const int32 Rem = static_cast<int32>(NumBytes - Offset);
	if (Rem > 0)
	{
		FMemory::Memcpy(Pending + PendingLen, Data + Offset, Rem);
		PendingLen += Rem;
	}
}

FString FMantlePlaceSha256::Final()
{
	// Buffered remainder + 0x80 + zero pad + 64-bit big-endian bit length.
	const uint64 BitLen = TotalBytes * 8;
	uint8 Tail[128] = { 0 };
	const int32 Rem = PendingLen;
	for (int32 I = 0; I < Rem; ++I)
	{
		Tail[I] = Pending[I];
	}
	Tail[Rem] = 0x80;
	const int32 TailBlocks = (Rem >= 56) ? 2 : 1;
	const int32 LenOffset = TailBlocks * 64 - 8;
	for (int32 I = 0; I < 8; ++I)
	{
		Tail[LenOffset + I] = static_cast<uint8>((BitLen >> (56 - I * 8)) & 0xFF);
	}
	for (int32 Block = 0; Block < TailBlocks; ++Block)
	{
		Sha256ProcessBlock(H, Tail + Block * 64);
	}

	FString Out;
	Out.Reserve(64);
	for (int32 I = 0; I < 8; ++I)
	{
		Out += FString::Printf(TEXT("%08x"), H[I]);
	}
	return Out;
}

FString FMantlePlaceBundleCacheLogic::Sha256Hex(const uint8* Data, int64 NumBytes)
{
	// One-shot convenience over the incremental hasher (single source of truth for the algorithm).
	FMantlePlaceSha256 Hasher;
	Hasher.Update(Data, NumBytes);
	return Hasher.Final();
}

FString FMantlePlaceBundleCacheLogic::SerializeMeta(const FMantlePlaceCachedBundle& Meta)
{
	// State is recomputed at inspect time (it depends on the live vault item), so it is NOT
	// persisted - the sidecar records only the integrity facts about the cached bytes.
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("orderId"), Meta.OrderId);
	Root->SetStringField(TEXT("localPath"), Meta.LocalPath);
	Root->SetStringField(TEXT("sha256"), Meta.Sha256);
	Root->SetNumberField(TEXT("sizeBytes"), static_cast<double>(Meta.SizeBytes));
	Root->SetNumberField(TEXT("manifestVersion"), Meta.ManifestVersion);
	Root->SetStringField(TEXT("downloadedAtUtc"), Meta.DownloadedAtUtc);
	Root->SetStringField(TEXT("format"), Meta.Format);
	return BundleCacheSerializeCondensed(Root);
}

bool FMantlePlaceBundleCacheLogic::ParseMeta(const FString& Json, FMantlePlaceCachedBundle& Out, FString& OutError)
{
	const TSharedPtr<FJsonObject> Root = BundleCacheDeserializeObject(Json);
	if (!Root.IsValid())
	{
		OutError = TEXT("Invalid JSON in cache sidecar");
		return false;
	}

	FMantlePlaceCachedBundle Parsed;
	if (!Root->TryGetStringField(TEXT("orderId"), Parsed.OrderId) || Parsed.OrderId.IsEmpty())
	{
		OutError = TEXT("Cache sidecar missing required 'orderId'");
		return false;
	}

	Root->TryGetStringField(TEXT("localPath"), Parsed.LocalPath);
	Root->TryGetStringField(TEXT("sha256"), Parsed.Sha256);

	double SizeBytes = 0.0;
	if (Root->TryGetNumberField(TEXT("sizeBytes"), SizeBytes))
	{
		Parsed.SizeBytes = static_cast<int64>(SizeBytes);
	}

	double ManifestVersion = 0.0;
	if (Root->TryGetNumberField(TEXT("manifestVersion"), ManifestVersion))
	{
		Parsed.ManifestVersion = static_cast<int32>(ManifestVersion);
	}

	Root->TryGetStringField(TEXT("downloadedAtUtc"), Parsed.DownloadedAtUtc);
	Root->TryGetStringField(TEXT("format"), Parsed.Format);

	Out = MoveTemp(Parsed);
	return true;
}
