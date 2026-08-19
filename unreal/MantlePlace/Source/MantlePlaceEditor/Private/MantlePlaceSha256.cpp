// Copyright Mantle Place. All Rights Reserved.

#include "MantlePlaceSha256.h"

namespace
{
	// --- Self-contained SHA-256 (FIPS 180-4). ---
	// Kept local so this Editor utility needs no platform/engine crypto: FPlatformMisc::GetSHA256Signature
	// asserts "No SHA256 Platform implementation" on Windows in UE 5.8. Duplicate of the proven
	// implementation in MantlePlaceRuntime/MantlePlaceAuthLogic.cpp (verified there against the RFC 7636
	// Appendix B vector); the NIST vectors in MantlePlaceSha256Test verify this copy end-to-end.
	struct FSha256Context
	{
		uint32 State[8];
		uint64 BitCount;
		uint8 Block[64];
		int32 BlockLen;

		static FORCEINLINE uint32 Ror(uint32 X, uint32 N) { return (X >> N) | (X << (32 - N)); }

		void Init()
		{
			State[0] = 0x6a09e667; State[1] = 0xbb67ae85; State[2] = 0x3c6ef372; State[3] = 0xa54ff53a;
			State[4] = 0x510e527f; State[5] = 0x9b05688c; State[6] = 0x1f83d9ab; State[7] = 0x5be0cd19;
			BitCount = 0;
			BlockLen = 0;
		}

		void Transform(const uint8* Chunk)
		{
			static const uint32 K[64] = {
				0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
				0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
				0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
				0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
				0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
				0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
				0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
				0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
			};

			uint32 W[64];
			for (int32 i = 0; i < 16; ++i)
			{
				W[i] = (uint32(Chunk[i * 4]) << 24) | (uint32(Chunk[i * 4 + 1]) << 16)
					| (uint32(Chunk[i * 4 + 2]) << 8) | uint32(Chunk[i * 4 + 3]);
			}
			for (int32 i = 16; i < 64; ++i)
			{
				const uint32 S0 = Ror(W[i - 15], 7) ^ Ror(W[i - 15], 18) ^ (W[i - 15] >> 3);
				const uint32 S1 = Ror(W[i - 2], 17) ^ Ror(W[i - 2], 19) ^ (W[i - 2] >> 10);
				W[i] = W[i - 16] + S0 + W[i - 7] + S1;
			}

			uint32 a = State[0], b = State[1], c = State[2], d = State[3];
			uint32 e = State[4], f = State[5], g = State[6], h = State[7];
			for (int32 i = 0; i < 64; ++i)
			{
				const uint32 Sig1 = Ror(e, 6) ^ Ror(e, 11) ^ Ror(e, 25);
				const uint32 Ch = (e & f) ^ (~e & g);
				const uint32 T1 = h + Sig1 + Ch + K[i] + W[i];
				const uint32 Sig0 = Ror(a, 2) ^ Ror(a, 13) ^ Ror(a, 22);
				const uint32 Maj = (a & b) ^ (a & c) ^ (b & c);
				const uint32 T2 = Sig0 + Maj;
				h = g; g = f; f = e; e = d + T1; d = c; c = b; b = a; a = T1 + T2;
			}

			State[0] += a; State[1] += b; State[2] += c; State[3] += d;
			State[4] += e; State[5] += f; State[6] += g; State[7] += h;
		}

		void Update(const uint8* Data, int32 Len)
		{
			for (int32 i = 0; i < Len; ++i)
			{
				Block[BlockLen++] = Data[i];
				if (BlockLen == 64)
				{
					Transform(Block);
					BitCount += 512;
					BlockLen = 0;
				}
			}
		}

		void Final(uint8 OutHash[32])
		{
			const uint64 TotalBits = BitCount + static_cast<uint64>(BlockLen) * 8;

			Block[BlockLen++] = 0x80; // append the '1' bit
			if (BlockLen > 56)
			{
				while (BlockLen < 64) { Block[BlockLen++] = 0; }
				Transform(Block);
				BlockLen = 0;
			}
			while (BlockLen < 56) { Block[BlockLen++] = 0; }

			for (int32 i = 7; i >= 0; --i) // 64-bit big-endian length
			{
				Block[BlockLen++] = static_cast<uint8>((TotalBits >> (i * 8)) & 0xFF);
			}
			Transform(Block);

			for (int32 i = 0; i < 8; ++i)
			{
				OutHash[i * 4]     = static_cast<uint8>((State[i] >> 24) & 0xFF);
				OutHash[i * 4 + 1] = static_cast<uint8>((State[i] >> 16) & 0xFF);
				OutHash[i * 4 + 2] = static_cast<uint8>((State[i] >> 8) & 0xFF);
				OutHash[i * 4 + 3] = static_cast<uint8>(State[i] & 0xFF);
			}
		}
	};
}

FString MantlePlaceSha256::HexDigest(TConstArrayView<uint8> Bytes)
{
	FSha256Context Ctx;
	Ctx.Init();
	Ctx.Update(Bytes.GetData(), Bytes.Num());
	uint8 Hash[32];
	Ctx.Final(Hash);

	FString Hex;
	Hex.Reserve(64);
	for (int32 i = 0; i < 32; ++i)
	{
		Hex.Appendf(TEXT("%02x"), Hash[i]);
	}
	return Hex;
}
